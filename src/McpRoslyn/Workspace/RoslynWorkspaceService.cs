using System.Collections.Concurrent;
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
    int PendingFileChanges);

/// <summary>
/// Owns the MSBuildWorkspace lifecycle and an immutable <see cref="Solution"/> snapshot that is
/// kept in sync with the file system: .cs/.vb edits are applied via WithDocumentText before every
/// query; project-file changes flag the workspace stale (a full reload is needed for those).
/// Strictly read-only: no API here ever writes to an analyzed file.
/// </summary>
public sealed class RoslynWorkspaceService(ILogger<RoslynWorkspaceService> log) : IDisposable
{
    private static readonly string[] SolutionExtensions = [".sln", ".slnx"];
    private static readonly string[] ProjectExtensions = [".csproj", ".vbproj"];
    private static readonly string[] SourceExtensions = [".cs", ".vb"];
    private static readonly string[] BuildFileExtensions = [".csproj", ".vbproj", ".props", ".targets", ".sln", ".slnx"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<(string Path, WatcherChangeTypes Kind)> _pendingFiles = new();
    private readonly ConcurrentDictionary<string, byte> _changedProjectFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<string> _loadDiagnostics = [];

    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private Task? _loadTask;
    private volatile WorkspaceState _state = WorkspaceState.NotLoaded;
    private string? _loadError;
    private string? _solutionPath;
    private DateTime? _loadedAtUtc;
    private TimeSpan? _loadDuration;
    private int _projectsLoaded;
    private string? _currentLoadingProject;
    private volatile bool _watcherOverflow;

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
        var target = ResolveLoadTarget(path);

        if (!force
            && _state == WorkspaceState.Loaded
            && string.Equals(_solutionPath, target, StringComparison.OrdinalIgnoreCase))
        {
            return $"Already loaded: {target}. Pass force_reload=true to reload.";
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check under the gate (another caller may have just finished the same load).
            if (!force
                && _state == WorkspaceState.Loaded
                && string.Equals(_solutionPath, target, StringComparison.OrdinalIgnoreCase))
            {
                return $"Already loaded: {target}.";
            }

            var task = LoadCoreAsync(target, progress, ct);
            _loadTask = task;
            await task.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        var status = GetStatus();
        var diagNote = status.LoadDiagnostics.Count > 0
            ? $" {status.LoadDiagnostics.Count} workspace warning(s) — see workspace_status."
            : "";
        var restoreNote = DetectRestoreProblem() ? " Some projects have no metadata references — run 'dotnet restore' on the solution and reload." : "";
        var projects = _solution!.Projects.ToList();
        return $"Loaded {target}: {projects.Count} project(s) in {_loadDuration!.Value.TotalSeconds:F1}s.{diagNote}{restoreNote}";
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

    private async Task LoadCoreAsync(string target, Action<string>? progress, CancellationToken ct)
    {
        DisposeWorkspace();
        _state = WorkspaceState.Loading;
        _loadError = null;
        _solutionPath = target;
        _projectsLoaded = 0;
        _currentLoadingProject = null;
        lock (_loadDiagnostics)
            _loadDiagnostics.Clear();
        _changedProjectFiles.Clear();
        while (_pendingFiles.TryDequeue(out _)) { }

        var started = DateTime.UtcNow;
        try
        {
            var workspace = MSBuildWorkspace.Create();
            _workspace = workspace;
            workspace.RegisterWorkspaceFailedHandler(e =>
            {
                lock (_loadDiagnostics)
                {
                    if (_loadDiagnostics.Count < 500)
                        _loadDiagnostics.Add($"{e.Diagnostic.Kind}: {e.Diagnostic.Message}");
                }
            });

            var loadProgress = new Progress<ProjectLoadProgress>(p =>
            {
                var name = Path.GetFileNameWithoutExtension(p.FilePath);
                _currentLoadingProject = name;
                if (p.Operation == ProjectLoadOperation.Resolve)
                {
                    Interlocked.Increment(ref _projectsLoaded);
                    progress?.Invoke($"{name}{(p.TargetFramework is null ? "" : $" ({p.TargetFramework})")}");
                }
            });

            Solution solution;
            var ext = Path.GetExtension(target);
            if (SolutionExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                solution = await workspace.OpenSolutionAsync(target, loadProgress, ct).ConfigureAwait(false);
            }
            else
            {
                var project = await workspace.OpenProjectAsync(target, loadProgress, ct).ConfigureAwait(false);
                solution = project.Solution;
            }

            _solution = solution;
            _loadedAtUtc = DateTime.UtcNow;
            _loadDuration = DateTime.UtcNow - started;
            _state = WorkspaceState.Loaded;
            SetUpWatchers(solution);
            log.LogInformation("Loaded {Path}: {Count} projects in {Secs:F1}s",
                target, solution.ProjectIds.Count, _loadDuration.Value.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state = WorkspaceState.Failed;
            _loadError = ex.Message;
            log.LogError(ex, "Failed to load {Path}", target);
            throw new ToolException($"Failed to load {target}: {ex.Message}");
        }
    }

    private bool DetectRestoreProblem()
    {
        var solution = _solution;
        if (solution is null)
            return false;
        return solution.Projects.Any(p => !p.MetadataReferences.Any() && p.DocumentIds.Count > 0);
    }

    // ---------------------------------------------------------------- querying

    /// <summary>
    /// Returns the current solution snapshot with all pending file edits applied.
    /// Waits a bounded time if a load is in flight; throws a teaching error otherwise.
    /// </summary>
    public async Task<Solution> GetSolutionAsync(CancellationToken ct, TimeSpan? maxWait = null)
    {
        var wait = maxWait ?? TimeSpan.FromSeconds(25);

        if (_state == WorkspaceState.NotLoaded)
        {
            // Try a lazy auto-load if discovery is unambiguous (startup may have been skipped/raced).
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
            _pendingFiles.Count);
    }

    /// <summary>
    /// One-line staleness warning tools append to their output when project files changed after
    /// load (those changes cannot be applied incrementally), or null when fresh.
    /// </summary>
    public string? StaleNotice =>
        _watcherOverflow
            ? "⚠ file-watcher overflow — results may be stale; call load_solution with force_reload=true."
            : !_changedProjectFiles.IsEmpty
                ? $"⚠ {_changedProjectFiles.Count} project file(s) changed since load (e.g. {Path.GetFileName(_changedProjectFiles.Keys.First())}) — results may be stale; call load_solution with force_reload=true."
                : null;

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
        _watcherOverflow = false;

        var roots = new List<string>();
        var solutionDir = SolutionDirectory;
        if (solutionDir is not null)
            roots.Add(solutionDir);

        foreach (var project in solution.Projects)
        {
            var dir = Path.GetDirectoryName(project.FilePath);
            if (dir is null)
                continue;
            if (!roots.Any(r => IsUnder(dir, r)))
                roots.Add(dir);
        }

        // Reduce to a minimal, capped set of roots.
        roots = roots
            .OrderBy(r => r.Length)
            .Aggregate(new List<string>(), (acc, r) =>
            {
                if (!acc.Any(existing => IsUnder(r, existing)))
                    acc.Add(r);
                return acc;
            });

        foreach (var root in roots.Take(10))
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
                watcher.Renamed += (_, e) =>
                {
                    OnFsEvent(e.OldFullPath, WatcherChangeTypes.Deleted);
                    OnFsEvent(e.FullPath, WatcherChangeTypes.Created);
                };
                watcher.Error += (_, _) => _watcherOverflow = true;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not watch {Root}; incremental sync limited", root);
            }
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
            _changedProjectFiles.TryAdd(fullPath, 0);
        }
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
        if (_pendingFiles.IsEmpty)
            return _solution ?? throw new ToolException("No solution loaded. Call load_solution first.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var solution = _solution ?? throw new ToolException("No solution loaded. Call load_solution first.");

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

            foreach (var (path, kind) in changes)
            {
                ct.ThrowIfCancellationRequested();
                var docIds = solution.GetDocumentIdsWithFilePath(path);

                if (kind == WatcherChangeTypes.Deleted || !File.Exists(path))
                {
                    foreach (var id in docIds)
                        solution = solution.RemoveDocument(id);
                    continue;
                }

                var text = TryReadFile(path);
                if (text is null)
                {
                    // Unreadable right now (editor lock etc.) — requeue for the next query.
                    _pendingFiles.Enqueue((path, kind));
                    continue;
                }

                if (docIds.Length > 0)
                {
                    foreach (var id in docIds)
                        solution = solution.WithDocumentText(id, text, PreservationMode.PreserveValue);
                }
                else
                {
                    // New file: attach to every project (all TFM flavors) whose directory contains it
                    // and whose language matches. SDK-style projects glob sources, so this mirrors
                    // what a real reload would produce.
                    var language = Path.GetExtension(path).Equals(".vb", StringComparison.OrdinalIgnoreCase)
                        ? LanguageNames.VisualBasic
                        : LanguageNames.CSharp;

                    var owners = solution.Projects
                        .Where(p => p.Language == language && p.FilePath is not null
                                    && IsUnder(path, Path.GetDirectoryName(p.FilePath)!))
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
                }
            }

            _solution = solution;
            return solution;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SourceText? TryReadFile(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
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

    private void DisposeWorkspace()
    {
        ClearWatchers();
        _workspace?.Dispose();
        _workspace = null;
        _solution = null;
    }

    public void Dispose()
    {
        DisposeWorkspace();
        _gate.Dispose();
    }
}
