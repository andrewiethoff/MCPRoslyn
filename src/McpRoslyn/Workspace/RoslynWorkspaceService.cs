using System.Collections.Concurrent;
using System.Xml.Linq;
using McpRoslyn.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace McpRoslyn.Workspace;

public enum WorkspaceState
{
    NotLoaded,
    Loading,
    Loaded,
    Failed,
}

public sealed record WorkspaceStatusInfo(
    WorkspaceState State,
    string? SolutionPath,
    DateTime? LoadedAtUtc,
    TimeSpan? LoadDuration,
    int ProjectsLoadedSoFar,
    string? CurrentlyLoadingProject,
    string? LoadError,
    IReadOnlyList<string> LoadDiagnostics,
    IReadOnlyList<string> ChangedProjectFiles,
    int PendingFileChanges,
    string? ReloadInFlightTarget,
    int UnwatchedRoots,
    IReadOnlyList<string> UnreadableFiles);

/// <summary>
/// Owns the MSBuildWorkspace lifecycle and an immutable <see cref="Solution"/> snapshot that is
/// kept in sync with the file system: .cs/.vb edits are applied via WithDocumentText before every
/// query; project-file changes flag the workspace stale (a full reload is needed for those).
/// A reload keeps serving the previous snapshot until the new one is ready, and a failed reload
/// never destroys a working workspace. Strictly read-only: nothing here writes to analyzed files.
/// </summary>
public sealed class RoslynWorkspaceService(ILogger<RoslynWorkspaceService> log) : IDisposable
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj"];
    private static readonly string[] SourceExtensions = [".cs", ".vb"];
    private static readonly string[] BuildFileExtensions = [".csproj", ".vbproj", ".props", ".targets", ".sln", ".slnx"];

    private const int MaxWatcherRoots = 10;
    private const int MaxReadAttemptsPerRound = 3;
    private const int MaxReadRounds = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<(string Path, WatcherChangeTypes Kind)> _pendingFiles = new();
    // Value = UTC time the change was last observed, so a reload can drop only the entries older
    // than its start and keep ones that arrive mid-load (a plain set would lose the latter).
    private readonly ConcurrentDictionary<string, DateTime> _changedProjectFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _readFailureRounds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unreadableFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _implicitCompileItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _loadDiagnostics = [];

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private Task? _loadTask;
    private volatile WorkspaceState _state = WorkspaceState.NotLoaded;
    private string? _loadError;
    private string? _solutionPath;
    private string? _activeLoadTarget;
    private DateTime? _loadedAtUtc;
    private TimeSpan? _loadDuration;
    private DateTime _lastSyncUtc = DateTime.UtcNow;
    private int _projectsLoaded;
    private string? _currentLoadingProject;
    private volatile bool _resyncNeeded;
    private volatile bool _disposed;
    private int _unwatchedRoots;

    public WorkspaceState State => _state;

    public string? SolutionPath => _solutionPath;

    public string? SolutionDirectory =>
        _solutionPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(_solutionPath));

    /// <summary>Working directory the host agent launched us in (Claude Code sets CLAUDE_PROJECT_DIR).</summary>
    public static string StartDirectory =>
        Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR") is { Length: > 0 } dir && Directory.Exists(dir)
            ? dir
            : Environment.CurrentDirectory;

    // ---------------------------------------------------------------- loading

    public async Task AutoLoadOnStartupAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("MCPROSLYN_AUTOLOAD") == "0")
        {
            log.LogInformation("Auto-load disabled via MCPROSLYN_AUTOLOAD=0");
            return;
        }

        var pinned = Environment.GetEnvironmentVariable("MCPROSLYN_SOLUTION");
        if (!string.IsNullOrWhiteSpace(pinned) && File.Exists(pinned))
        {
            await LoadAsync(pinned, force: false, progress: null, ct).ConfigureAwait(false);
            return;
        }

        var candidates = DiscoverCandidates(StartDirectory);
        if (candidates.Count == 1)
        {
            log.LogInformation("Auto-discovered {Path}; loading in background", candidates[0]);
            await LoadAsync(candidates[0], force: false, progress: null, ct).ConfigureAwait(false);
        }
        else
        {
            log.LogInformation(
                "No unambiguous solution found under {Dir} ({Count} candidates); waiting for load_solution",
                StartDirectory, candidates.Count);
        }
    }

    /// <summary>
    /// Finds solution/project candidates: walks up from <paramref name="startDir"/>, at each level
    /// collecting *.sln/*.slnx (stopping at the first level that has any); if none found all the
    /// way up, does the same walk for *.csproj/*.vbproj.
    /// </summary>
    public static List<string> DiscoverCandidates(string startDir)
    {
        foreach (var extensions in new[] { SolutionExtensions, ProjectExtensions })
        {
            for (var dir = new DirectoryInfo(Path.GetFullPath(startDir)); dir is not null; dir = dir.Parent)
            {
                List<string> found;
                try
                {
                    found = dir.EnumerateFiles()
                        .Where(f => extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                        .Select(f => f.FullName)
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }

                if (found.Count > 0)
                    return found;
            }
        }

        return [];
    }

    /// <summary>Loads (or reloads) a solution/project. Serialized; only one load at a time.</summary>
    public async Task<string> LoadAsync(string? path, bool force, Action<string>? progress, CancellationToken ct)
    {
        if (_disposed)
            throw new ToolException("Server is shutting down.");

        var target = ResolveLoadTarget(path);

        if (!force
            && _state == WorkspaceState.Loaded
            && _activeLoadTarget is null
            && string.Equals(_solutionPath, target, StringComparison.OrdinalIgnoreCase))
        {
            return $"Already loaded: {target}. Pass force_reload=true to reload.";
        }

        // A load of the same target is already running: report instead of blocking on the gate
        // for its whole duration with zero progress notifications.
        if (!force
            && string.Equals(_activeLoadTarget, target, StringComparison.OrdinalIgnoreCase))
        {
            return $"Load already in progress for {target} — {_projectsLoaded} project(s) resolved so far"
                   + (_currentLoadingProject is null ? "" : $", currently {_currentLoadingProject}")
                   + ". Check workspace_status or retry shortly.";
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force
                && _state == WorkspaceState.Loaded
                && string.Equals(_solutionPath, target, StringComparison.OrdinalIgnoreCase))
            {
                return $"Already loaded: {target}.";
            }

            var task = LoadCoreAsync(target, progress, ct);
            _loadTask = task;
            return await task.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Host shut down while we held the gate.
            }
        }
    }

    private string ResolveLoadTarget(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var candidates = DiscoverCandidates(StartDirectory);
            return candidates.Count switch
            {
                1 => candidates[0],
                0 => throw new ToolException(
                    $"No .sln/.slnx/.csproj/.vbproj found at or above {StartDirectory}. Call load_solution with an explicit path."),
                _ => throw new ToolException(
                    "Multiple candidates found — call load_solution with one of:\n  " + string.Join("\n  ", candidates)),
            };
        }

        var full = Path.GetFullPath(path, StartDirectory);
        if (Directory.Exists(full))
        {
            var candidates = DiscoverCandidates(full);
            if (candidates.Count == 1)
                return candidates[0];
            throw new ToolException(candidates.Count == 0
                ? $"No solution or project file found under {full}."
                : "Multiple candidates under that directory — pick one:\n  " + string.Join("\n  ", candidates));
        }

        if (!File.Exists(full))
            throw new ToolException($"File not found: {full}");

        var ext = Path.GetExtension(full);
        if (!SolutionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
            && !ProjectExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            throw new ToolException($"Unsupported file type '{ext}' — expected .sln, .slnx, .csproj or .vbproj.");
        }

        return full;
    }

    private async Task<string> LoadCoreAsync(string target, Action<string>? progress, CancellationToken ct)
    {
        _activeLoadTarget = target;
        _projectsLoaded = 0;
        _currentLoadingProject = null;
        var hadSolution = _solution is not null;
        if (!hadSolution)
            _state = WorkspaceState.Loading;

        // Project-file changes recorded before this load began are covered by the fresh load itself;
        // anything arriving DURING the load must survive it. Compare timestamps rather than a
        // captured key set so a same-path edit that re-fires mid-load is not silently dropped.
        var loadStartUtc = DateTime.UtcNow;

        // Watch the target's directory during the load so edits made while loading are queued and
        // reconciled by the normal sync path afterwards (no dead zone). The old watchers keep
        // running for the old snapshot until the swap.
        var provisionalWatchers = new List<FileSystemWatcher>();
        TryAddWatcher(provisionalWatchers, Path.GetDirectoryName(Path.GetFullPath(target))!);

        var started = DateTime.UtcNow;
        MSBuildWorkspace? newWorkspace = null;
        var newDiagnostics = new List<string>();

        try
        {
            newWorkspace = MSBuildWorkspace.Create();
            newWorkspace.RegisterWorkspaceFailedHandler(e =>
            {
                lock (newDiagnostics)
                {
                    if (newDiagnostics.Count < 500)
                        newDiagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
                }
            });

            var loadProgress = new Progress<ProjectLoadProgress>(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p.FilePath);
                _currentLoadingProject = name;
                if (p.Operation == ProjectLoadOperation.Resolve)
                    Interlocked.Increment(ref _projectsLoaded);
                progress?.Invoke($"{name} — {p.Operation}{(p.TargetFramework is null ? "" : $" ({p.TargetFramework})")}");
            });

            Solution solution;
            var ext = Path.GetExtension(target);
            if (SolutionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                solution = await newWorkspace.OpenSolutionAsync(target, loadProgress, ct).ConfigureAwait(false);
            }
            else
            {
                var project = await newWorkspace.OpenProjectAsync(target, loadProgress, ct).ConfigureAwait(false);
                solution = project.Solution;
            }

            // Success — swap. Assign the new snapshot before disposing the old workspace so
            // concurrent readers never observe null.
            var oldWorkspace = _workspace;
            _solution = solution;
            _workspace = newWorkspace;
            newWorkspace = null; // ownership transferred
            _solutionPath = target;
            _loadedAtUtc = DateTime.UtcNow;
            _loadDuration = DateTime.UtcNow - started;
            _lastSyncUtc = started;
            _loadError = null;
            _state = WorkspaceState.Loaded;

            lock (_loadDiagnostics)
            {
                _loadDiagnostics.Clear();
                _loadDiagnostics.AddRange(newDiagnostics);
            }

            foreach (var entry in _changedProjectFiles.ToArray())
                if (entry.Value < loadStartUtc)
                    _changedProjectFiles.TryRemove(entry); // atomic: keeps entries re-touched mid-load
            _readFailureRounds.Clear();
            _unreadableFiles.Clear();

            SetUpWatchers(solution);
            oldWorkspace?.Dispose();

            log.LogInformation("Loaded {Path}: {Count} projects in {Secs:F1}s",
                target, solution.ProjectIds.Count, _loadDuration.Value.TotalSeconds);

            var projectCount = solution.Projects
                .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
                .Count();
            var diagNote = newDiagnostics.Count > 0
                ? $" {newDiagnostics.Count} workspace warning(s) — see workspace_status."
                : "";
            var restoreNote = solution.Projects.Any(p => !p.MetadataReferences.Any() && p.DocumentIds.Count > 0)
                ? " Some projects have no metadata references — run 'dotnet restore' on the solution and reload."
                : "";
            return $"Loaded {target}: {projectCount} project(s) in {_loadDuration.Value.TotalSeconds:F1}s.{diagNote}{restoreNote}";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            newWorkspace?.Dispose();
            log.LogError(ex, "Failed to load {Path}", target);

            if (hadSolution)
            {
                // Keep serving the previous, still-valid snapshot.
                _state = WorkspaceState.Loaded;
                throw new ToolException(
                    $"Failed to load {target}: {ex.Message}. The previously loaded solution ({_solutionPath}) remains active.");
            }

            _state = WorkspaceState.Failed;
            _loadError = ex.Message;
            throw new ToolException($"Failed to load {target}: {ex.Message}");
        }
        finally
        {
            _activeLoadTarget = null;
            _currentLoadingProject = null;
            foreach (var watcher in provisionalWatchers)
            {
                try
                {
                    watcher.Dispose();
                }
                catch
                {
                    // Best-effort.
                }
            }
        }
    }

    // ---------------------------------------------------------------- querying

    /// <summary>
    /// Returns the current solution snapshot with all pending file edits applied.
    /// Waits a bounded time if the initial load is in flight; a reload in flight keeps serving
    /// the previous snapshot.
    /// </summary>
    public async Task<Solution> GetSolutionAsync(CancellationToken ct, TimeSpan? maxWait = null)
    {
        var wait = maxWait ?? TimeSpan.FromSeconds(25);

        if (_state == WorkspaceState.NotLoaded && _activeLoadTarget is null)
        {
            // Lazy auto-load if discovery is unambiguous (startup may have been skipped/raced).
            var candidates = DiscoverCandidates(StartDirectory);
            if (candidates.Count == 1)
                _ = Task.Run(() => LoadAsync(candidates[0], force: false, progress: null, CancellationToken.None), CancellationToken.None);
            else
                throw new ToolException(candidates.Count == 0
                    ? $"No solution loaded and none found at or above {StartDirectory}. Call load_solution with a path."
                    : "No solution loaded; multiple candidates found. Call load_solution with one of:\n  "
                      + string.Join("\n  ", candidates));
        }

        var deadline = DateTime.UtcNow + wait;
        while (_state is WorkspaceState.Loading or WorkspaceState.NotLoaded && DateTime.UtcNow < deadline)
        {
            var task = _loadTask;
            if (task is not null)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                    await Task.WhenAny(task, Task.Delay(remaining, ct)).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
        }

        switch (_state)
        {
            case WorkspaceState.Failed:
                throw new ToolException($"Solution load failed: {_loadError}. Fix the problem and call load_solution again.");
            case WorkspaceState.Loading:
            case WorkspaceState.NotLoaded:
                throw new ToolException(
                    $"Solution is still loading ({_projectsLoaded} project(s) resolved so far"
                    + (_currentLoadingProject is null ? "" : $", currently {_currentLoadingProject}")
                    + "). Retry in a moment, or check workspace_status.");
        }

        return await ApplyPendingChangesAsync(ct).ConfigureAwait(false);
    }

    public WorkspaceStatusInfo GetStatus()
    {
        List<string> diags;
        lock (_loadDiagnostics)
            diags = [.. _loadDiagnostics];

        return new WorkspaceStatusInfo(
            _state,
            _solutionPath,
            _loadedAtUtc,
            _loadDuration,
            _projectsLoaded,
            _currentLoadingProject,
            _loadError,
            diags,
            [.. _changedProjectFiles.Keys],
            _pendingFiles.Count,
            _activeLoadTarget,
            _unwatchedRoots,
            [.. _unreadableFiles.Keys]);
    }

    /// <summary>
    /// One-line staleness warning tools append to their output, or null when fresh.
    /// </summary>
    public string? StaleNotice
    {
        get
        {
            if (_activeLoadTarget is { } reloading && _solution is not null)
                return $"⚠ a (re)load of {Path.GetFileName(reloading)} is in progress — results reflect the previous snapshot.";

            var changedProject = _changedProjectFiles.Keys.FirstOrDefault();
            if (changedProject is not null)
                return $"⚠ {_changedProjectFiles.Count} project file(s) changed since load (e.g. {Path.GetFileName(changedProject)}) — results may be stale; call load_solution with force_reload=true.";

            var unreadable = _unreadableFiles.Keys.FirstOrDefault();
            if (unreadable is not null)
                return $"⚠ {_unreadableFiles.Count} changed file(s) could not be re-read (e.g. {Path.GetFileName(unreadable)}) — results for them may be stale.";

            if (_unwatchedRoots > 0)
                return $"⚠ {_unwatchedRoots} project director{(_unwatchedRoots == 1 ? "y is" : "ies are")} not file-watched — edits there are invisible until load_solution(force_reload:true).";

            return null;
        }
    }

    public string RelPath(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
            return "?";
        var baseDir = SolutionDirectory;
        if (baseDir is null)
            return absolutePath;
        var rel = Path.GetRelativePath(baseDir, absolutePath);
        return rel.StartsWith("..", StringComparison.Ordinal) ? absolutePath : rel.Replace('\\', '/');
    }

    // ---------------------------------------------------------------- file sync

    private void SetUpWatchers(Solution solution)
    {
        ClearWatchers();
        _resyncNeeded = false;
        _implicitCompileItems.Clear();
        Interlocked.Exchange(ref _unwatchedRoots, 0);

        // Watch the directory of every loaded document, not just project-file directories: a
        // project can pull in linked/shared source with a relative Compile Include that lives
        // outside its own folder, and edits there must still be seen.
        var candidateRoots = new List<string>();
        var solutionDir = SolutionDirectory;
        if (solutionDir is not null)
            candidateRoots.Add(solutionDir);

        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
        {
            if (Path.GetDirectoryName(project.FilePath) is { } projectDir)
                dirs.Add(projectDir);
            foreach (var document in project.Documents)
            {
                if (document.FilePath is { } docPath && Path.GetDirectoryName(docPath) is { } docDir)
                    dirs.Add(docDir);
            }
        }
        candidateRoots.AddRange(dirs);

        // Reduce to a minimal set of roots (drop any directory already covered by a parent).
        var roots = candidateRoots
            .OrderBy(r => r.Length)
            .Aggregate(new List<string>(), (acc, r) =>
            {
                if (!acc.Any(existing => IsUnder(r, existing)))
                    acc.Add(r);
                return acc;
            });

        if (roots.Count > MaxWatcherRoots)
            Interlocked.Add(ref _unwatchedRoots, roots.Count - MaxWatcherRoots);

        lock (_watchers)
        {
            foreach (var root in roots.Take(MaxWatcherRoots))
            {
                if (!TryAddWatcher(_watchers, root))
                    Interlocked.Increment(ref _unwatchedRoots);
            }
        }
    }

    private bool TryAddWatcher(List<FileSystemWatcher> target, string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024,
            };
            watcher.Changed += (_, e) => OnFsEvent(e.FullPath, WatcherChangeTypes.Changed);
            watcher.Created += (_, e) => OnFsEvent(e.FullPath, WatcherChangeTypes.Created);
            watcher.Deleted += (_, e) => OnFsEvent(e.FullPath, WatcherChangeTypes.Deleted);
            watcher.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
            watcher.Error += (_, _) => _resyncNeeded = true;
            watcher.EnableRaisingEvents = true;
            target.Add(watcher);
            return true;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not watch {Root}; incremental sync limited", root);
            return false;
        }
    }

    private static bool IsUnder(string path, string root) =>
        path.Length >= root.Length
        && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
        && (path.Length == root.Length || path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);

    private void OnFsEvent(string fullPath, WatcherChangeTypes kind)
    {
        var ext = Path.GetExtension(fullPath);
        if (ContainsIgnoredSegment(fullPath))
            return;

        if (SourceExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            _pendingFiles.Enqueue((fullPath, kind));
        }
        else if (BuildFileExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
        {
            _changedProjectFiles[fullPath] = DateTime.UtcNow;
        }
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        // A directory rename raises ONE event for the directory, never one per contained file, so
        // the plain (extension-based) path would drop it and leave every file under it stale.
        // Detect the directory case and reconcile: request a timestamp resync (which removes the
        // documents whose old paths vanished) and enqueue the renamed tree's source/build files.
        if (Directory.Exists(newPath))
        {
            _resyncNeeded = true;
            try
            {
                foreach (var file in Directory.EnumerateFiles(newPath, "*", SearchOption.AllDirectories))
                    OnFsEvent(file, WatcherChangeTypes.Created);
            }
            catch (Exception ex)
            {
                // Enumeration is best-effort; the resync flag still forces a timestamp reconcile.
                log.LogDebug(ex, "Could not enumerate renamed directory {Path}", newPath);
            }
            return;
        }

        OnFsEvent(oldPath, WatcherChangeTypes.Deleted);
        OnFsEvent(newPath, WatcherChangeTypes.Created);
    }

    private static bool ContainsIgnoredSegment(string path)
    {
        foreach (var segment in new[] { "\\obj\\", "\\bin\\", "\\.git\\", "\\.vs\\", "/obj/", "/bin/", "/.git/", "/.vs/" })
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private async Task<Solution> ApplyPendingChangesAsync(CancellationToken ct)
    {
        if (_pendingFiles.IsEmpty && !_resyncNeeded)
            return _solution ?? throw new ToolException("No solution loaded. Call load_solution first.");

        // Bounded: if a (re)load holds the gate for minutes, serve the previous snapshot instead
        // of hanging past the client timeout. StaleNotice reports the reload separately.
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false))
        {
            return _solution ?? throw new ToolException(
                "Solution is (re)loading and the previous snapshot is not available yet. Retry in a moment, or check workspace_status.");
        }

        try
        {
            var solution = _solution ?? throw new ToolException("No solution loaded. Call load_solution first.");

            // Low-water mark for the next resync scan: capture BEFORE draining/processing. If a file
            // is modified while this apply runs and its watcher event is dropped (buffer overflow),
            // its mtime will exceed this timestamp and the next resync re-enqueues it. Advancing the
            // mark to post-processing time (DateTime.UtcNow at the end) would blind that window.
            var syncStartUtc = DateTime.UtcNow;

            if (_resyncNeeded)
            {
                // The watcher overflowed or errored: re-arm it and reconcile by timestamp so we
                // never trust a lossy event stream.
                SetUpWatchers(solution);
                foreach (var document in solution.Projects.SelectMany(p => p.Documents))
                {
                    if (document.FilePath is not { } filePath)
                        continue;
                    if (!File.Exists(filePath))
                        _pendingFiles.Enqueue((filePath, WatcherChangeTypes.Deleted));
                    else if (File.GetLastWriteTimeUtc(filePath) > _lastSyncUtc)
                        _pendingFiles.Enqueue((filePath, WatcherChangeTypes.Changed));
                }
            }

            // Coalesce: last event per path wins (a Created followed by Changed is just an upsert).
            var changes = new Dictionary<string, WatcherChangeTypes>(StringComparer.OrdinalIgnoreCase);
            while (_pendingFiles.TryDequeue(out var change))
            {
                changes[change.Path] = change.Kind == WatcherChangeTypes.Deleted
                    ? WatcherChangeTypes.Deleted
                    : changes.TryGetValue(change.Path, out var prev) && prev == WatcherChangeTypes.Created
                        ? WatcherChangeTypes.Created
                        : change.Kind;
            }

            try
            {
                foreach (var (path, kind) in changes)
                {
                    ct.ThrowIfCancellationRequested();
                    var docIds = solution.GetDocumentIdsWithFilePath(path);

                    if (kind == WatcherChangeTypes.Deleted || !File.Exists(path))
                    {
                        foreach (var id in docIds)
                            solution = solution.RemoveDocument(id);
                        _readFailureRounds.TryRemove(path, out _);
                        continue;
                    }

                    var text = await TryReadFileAsync(path, ct).ConfigureAwait(false);
                    if (text is null)
                    {
                        // Unreadable right now (editor lock etc.): retry on the next few queries,
                        // then give up loudly via StaleNotice instead of paying the retry forever.
                        var rounds = _readFailureRounds.AddOrUpdate(path, 1, (_, r) => r + 1);
                        if (rounds < MaxReadRounds)
                            _pendingFiles.Enqueue((path, kind));
                        else
                            _unreadableFiles.TryAdd(path, 0);
                        continue;
                    }

                    _readFailureRounds.TryRemove(path, out _);
                    _unreadableFiles.TryRemove(path, out _);

                    if (docIds.Length > 0)
                    {
                        foreach (var id in docIds)
                            solution = solution.WithDocumentText(id, text, PreservationMode.PreserveValue);
                    }
                    else
                    {
                        // New file: attach to the nearest matching project(s) — but only when that
                        // project uses SDK-style implicit globbing. Legacy projects with explicit
                        // <Compile Include> items (or SDK projects that disable default items) do
                        // not necessarily include a file just because it sits in the directory, so
                        // guessing membership would be wrong. For those, flag the project stale and
                        // let the user reload deliberately.
                        var language = Path.GetExtension(path).Equals(".vb", StringComparison.OrdinalIgnoreCase)
                            ? LanguageNames.VisualBasic
                            : LanguageNames.CSharp;

                        var owners = solution.Projects
                            .Where(p => p.Language == language && p.FilePath is not null
                                        && IsUnder(path, Path.GetDirectoryName(p.FilePath)!)
                                        && ProjectImplicitlyIncludesSources(p.FilePath!))
                            .GroupBy(p => Path.GetDirectoryName(p.FilePath)!, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Key.Length)
                            .FirstOrDefault();

                        if (owners is not null)
                        {
                            foreach (var project in owners)
                            {
                                solution = solution.AddDocument(
                                    DocumentId.CreateNewId(project.Id),
                                    Path.GetFileName(path),
                                    text,
                                    filePath: path);
                            }
                        }
                        else
                        {
                            var nearest = solution.Projects
                                .Where(p => p.FilePath is not null && IsUnder(path, Path.GetDirectoryName(p.FilePath)!))
                                .OrderByDescending(p => p.FilePath!.Length)
                                .FirstOrDefault();
                            if (nearest?.FilePath is { } projectFile)
                                _changedProjectFiles[projectFile] = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch
            {
                // Never lose edits: put everything back for the next query. Re-applying entries
                // that already made it into the local snapshot is harmless (contents are re-read).
                foreach (var (path, kind) in changes)
                    _pendingFiles.Enqueue((path, kind));
                throw;
            }

            _solution = solution;
            _lastSyncUtc = syncStartUtc;
            return solution;
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// True when a project uses SDK-style implicit compile globbing (so a source file appearing in
    /// its directory really does become part of it). False for legacy non-SDK projects with explicit
    /// Compile items, and for SDK projects that set EnableDefaultCompileItems/EnableDefaultItems to
    /// false. When we cannot tell, we answer false and require a deliberate reload — never guess a
    /// file into the wrong project. Cached per project file; the cache is cleared on every load.
    /// </summary>
    private bool ProjectImplicitlyIncludesSources(string projectFilePath) =>
        _implicitCompileItems.GetOrAdd(projectFilePath, static path =>
        {
            try
            {
                var root = XDocument.Load(path).Root;
                if (root is null)
                    return false;

                var isSdkStyle = root.Attribute("Sdk") is not null
                    || root.Elements().Any(e => e.Name.LocalName is "Sdk")
                    || root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null);
                if (!isSdkStyle)
                    return false;

                var defaultsDisabled = root.Descendants()
                    .Where(e => e.Name.LocalName is "EnableDefaultCompileItems" or "EnableDefaultItems")
                    .Any(e => string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
                if (defaultsDisabled)
                    return false;

                // A <Compile Remove .../> narrows the implicit glob, so a file in the directory is
                // not necessarily compiled. Be conservative and require a deliberate reload rather
                // than materialize a document the real build would exclude.
                var narrowsCompileItems = root.Descendants()
                    .Any(e => e.Name.LocalName == "Compile" && e.Attribute("Remove") is not null);
                return !narrowsCompileItems;
            }
            catch
            {
                return false;
            }
        });

    private static async Task<SourceText?> TryReadFileAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxReadAttemptsPerRound; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream);
            }
            catch (IOException) when (attempt < MaxReadAttemptsPerRound)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- helpers & cleanup

    /// <summary>Projects filtered by an optional case-insensitive name substring.</summary>
    public static IEnumerable<Project> FilterProjects(Solution solution, string? projectFilter) =>
        string.IsNullOrWhiteSpace(projectFilter)
            ? solution.Projects
            : solution.Projects.Where(p => p.Name.Contains(projectFilter, StringComparison.OrdinalIgnoreCase));

    private void ClearWatchers()
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch
                {
                    // Disposal is best-effort.
                }
            }
            _watchers.Clear();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ClearWatchers();
        _workspace?.Dispose();
        _workspace = null;
        _solution = null;
        _gate.Dispose();
    }
}
