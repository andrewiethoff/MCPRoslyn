using System.ComponentModel;
using System.Text;
using McpRoslyn.Infrastructure;
using McpRoslyn.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpRoslyn.Tools;

[McpServerToolType]
public static class WorkspaceTools
{
    [McpServerTool(Name = "load_solution")]
    [Description("Load (or switch to / reload) a .sln, .slnx, .csproj or .vbproj. Without a path, auto-discovers from the working directory. Loading a large solution can take a while; progress is reported. NuGet restore is NOT performed — run 'dotnet restore' first if the solution hasn't been built on this machine.")]
    public static Task<string> LoadSolution(
        RoslynWorkspaceService workspace,
        McpRoslyn.Decompilation.DecompilerService decompiler,
        ILoggerFactory loggerFactory,
        IProgress<ProgressNotificationValue> progress,
        CancellationToken ct,
        [Description("Path to a solution/project file or a directory to search. Omit to auto-discover.")]
        string? path = null,
        [Description("Reload even if this solution is already loaded (needed after .csproj changes).")]
        bool force_reload = false)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "load_solution", async () =>
        {
            var count = 0;
            var result = await workspace.LoadAsync(
                path,
                force_reload,
                // Progress<T> callbacks can run concurrently on the thread pool.
                name => progress.Report(new ProgressNotificationValue
                {
                    Progress = Interlocked.Increment(ref count),
                    Message = name,
                }),
                ct);
            // Assemblies may have been rebuilt/replaced since the previous load.
            decompiler.Clear();
            return result;
        });

    [McpServerTool(Name = "workspace_status")]
    [Description("Current workspace state: load progress, per-project info (TFMs, document/reference counts), load warnings (skipped or failed projects), and staleness. Check this when other tools report the solution is loading or results seem off.")]
    public static Task<string> WorkspaceStatus(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "workspace_status", async () =>
        {
            var status = workspace.GetStatus();
            var sb = new StringBuilder();
            sb.AppendLine($"state: {status.State}");
            if (status.SolutionPath is not null)
                sb.AppendLine($"solution: {status.SolutionPath}");

            switch (status.State)
            {
                case WorkspaceState.Loading:
                    sb.AppendLine($"loading: {status.ProjectsLoadedSoFar} project(s) resolved so far"
                                  + (status.CurrentlyLoadingProject is null ? "" : $", currently {status.CurrentlyLoadingProject}"));
                    break;
                case WorkspaceState.Failed:
                    sb.AppendLine($"error: {status.LoadError}");
                    break;
                case WorkspaceState.Loaded:
                    var solution = await workspace.GetSolutionAsync(ct, TimeSpan.FromSeconds(5));
                    sb.AppendLine($"loaded: {status.LoadedAtUtc:u} in {status.LoadDuration?.TotalSeconds:F1}s");
                    var groups = solution.Projects
                        .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    sb.AppendLine($"projects ({groups.Count}):");
                    foreach (var group in groups)
                    {
                        var first = group.First();
                        var tfms = group.Select(TfmOf).Where(t => t is not null).Distinct().ToList();
                        var restoreFlag = group.Any(p => !p.MetadataReferences.Any() && p.DocumentIds.Count > 0)
                            ? "  ⚠ no metadata references — needs restore?"
                            : "";
                        var language = first.Language == "Visual Basic" ? "VB" : first.Language;
                        sb.AppendLine($"  {first.Name}{(tfms.Count > 0 ? $" [{string.Join(", ", tfms)}]" : "")}"
                                      + $" — {language}, {first.DocumentIds.Count} files{restoreFlag}");
                    }
                    break;
            }

            if (status.ReloadInFlightTarget is not null)
                sb.AppendLine($"reload in progress: {status.ReloadInFlightTarget} ({status.ProjectsLoadedSoFar} projects resolved"
                              + (status.CurrentlyLoadingProject is null ? ")" : $", currently {status.CurrentlyLoadingProject})"));

            if (status.ChangedProjectFiles.Count > 0)
            {
                sb.AppendLine($"stale: {status.ChangedProjectFiles.Count} project file(s) changed since load — call load_solution(force_reload:true):");
                foreach (var file in status.ChangedProjectFiles.Take(10))
                    sb.AppendLine($"  {file}");
            }

            if (status.UnreadableFiles.Count > 0)
                sb.AppendLine($"unreadable: {status.UnreadableFiles.Count} changed file(s) could not be re-read (locked?): "
                              + string.Join(", ", status.UnreadableFiles.Take(5)));

            if (status.UnwatchedRoots > 0)
                sb.AppendLine($"unwatched: {status.UnwatchedRoots} project director(ies) beyond the watcher cap — edits there are invisible until a forced reload.");

            if (status.LoadDiagnostics.Count > 0)
            {
                sb.AppendLine($"load warnings ({status.LoadDiagnostics.Count}):");
                foreach (var diag in status.LoadDiagnostics.Take(20))
                    sb.AppendLine($"  {Truncate(diag, 300)}");
                if (status.LoadDiagnostics.Count > 20)
                    sb.AppendLine($"  … {status.LoadDiagnostics.Count - 20} more");
            }

            return sb.ToString().TrimEnd();
        });

    [McpServerTool(Name = "ping")]
    [Description("Liveness check: server version and a one-line workspace summary.")]
    public static string Ping(RoslynWorkspaceService workspace)
    {
        var version = typeof(WorkspaceTools).Assembly.GetName().Version?.ToString(3) ?? "?";
        var status = workspace.GetStatus();
        var detail = status.State switch
        {
            WorkspaceState.Loaded => $"{status.SolutionPath}",
            WorkspaceState.Loading => $"loading {status.SolutionPath} ({status.ProjectsLoadedSoFar} projects so far)",
            WorkspaceState.Failed => $"load FAILED: {status.LoadError}",
            _ => "no solution loaded",
        };
        return $"mcp-roslyn v{version} — {status.State}: {detail}";
    }

    private static string? TfmOf(Microsoft.CodeAnalysis.Project project)
    {
        // Roslyn names multi-TFM flavors "Name (net8.0)".
        var open = project.Name.LastIndexOf('(');
        return open >= 0 && project.Name.EndsWith(")", StringComparison.Ordinal)
            ? project.Name[(open + 1)..^1]
            : null;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
