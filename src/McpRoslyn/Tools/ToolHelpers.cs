using McpRoslyn.Infrastructure;
using McpRoslyn.Workspace;
using Microsoft.CodeAnalysis;

namespace McpRoslyn.Tools;

internal static class ToolHelpers
{
    /// <summary>
    /// Finds a document by path: absolute, solution-relative, or unique filename/suffix match.
    /// Multi-TFM projects contain the same file several times; this returns the first (they share
    /// identical text), <see cref="FindAllDocuments"/> returns every flavor.
    /// </summary>
    public static Document FindDocument(Solution solution, RoslynWorkspaceService workspace, string file)
    {
        var all = FindAllDocuments(solution, workspace, file);
        return all[0];
    }

    public static IReadOnlyList<Document> FindAllDocuments(Solution solution, RoslynWorkspaceService workspace, string file)
    {
        var normalized = file.Replace('/', Path.DirectorySeparatorChar);

        foreach (var candidate in PathCandidates(workspace, normalized))
        {
            var ids = solution.GetDocumentIdsWithFilePath(candidate);
            if (ids.Length > 0)
                return ids.Select(id => solution.GetDocument(id)).Where(d => d is not null).Cast<Document>().ToList();
        }

        // Suffix match over all documents (agents often pass partial paths).
        var suffix = normalized.TrimStart(Path.DirectorySeparatorChar);
        var matches = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath is not null
                        && d.FilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var distinctPaths = matches.Select(d => d.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        switch (distinctPaths.Count)
        {
            case 1:
                return matches;
            case 0:
                throw new ToolException($"File '{file}' is not part of the loaded solution. Paths are relative to {workspace.SolutionDirectory}.");
            default:
                throw new ToolException(
                    $"'{file}' matches {distinctPaths.Count} files — be more specific:\n  "
                    + string.Join("\n  ", distinctPaths.Take(10).Select(workspace.RelPath)));
        }
    }

    private static IEnumerable<string> PathCandidates(RoslynWorkspaceService workspace, string file)
    {
        if (Path.IsPathRooted(file))
        {
            yield return Path.GetFullPath(file);
            yield break;
        }
        if (workspace.SolutionDirectory is { } solutionDir)
            yield return Path.GetFullPath(Path.Combine(solutionDir, file));
        yield return Path.GetFullPath(Path.Combine(RoslynWorkspaceService.StartDirectory, file));
    }

    public static Project? ProjectOf(Solution solution, ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.SourceTree is { } tree && solution.GetDocument(tree) is { } document)
                return document.Project;
        }
        return null;
    }

    /// <summary>Resolves a project by exact or substring name match.</summary>
    public static Project FindProject(Solution solution, string name)
    {
        var exact = solution.Projects
            .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exact.Count > 0)
            return exact[0];

        var partial = solution.Projects
            .Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return partial.Count switch
        {
            1 => partial[0].First(),
            0 => throw new ToolException(
                $"No project matching '{name}'. Projects: {string.Join(", ", solution.Projects.Select(p => p.Name).Distinct().Take(30))}"),
            _ => throw new ToolException(
                $"'{name}' matches several projects: {string.Join(", ", partial.Select(g => g.First().Name).Take(10))}"),
        };
    }

    public static string SeverityShort(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "ERR",
        DiagnosticSeverity.Warning => "WRN",
        DiagnosticSeverity.Info => "INF",
        _ => "HID",
    };

    public static DiagnosticSeverity ParseMinSeverity(string? minSeverity) => minSeverity?.ToLowerInvariant() switch
    {
        null or "" or "warning" or "warn" => DiagnosticSeverity.Warning,
        "error" => DiagnosticSeverity.Error,
        "info" => DiagnosticSeverity.Info,
        "hidden" or "all" => DiagnosticSeverity.Hidden,
        _ => throw new ToolException($"Unknown min_severity '{minSeverity}' — use error|warning|info|hidden."),
    };

    /// <summary>Stable dedupe key for a source location (multi-TFM projects yield duplicates).</summary>
    public static string LocationKey(Location location)
    {
        var span = location.GetLineSpan();
        return $"{span.Path}|{location.SourceSpan.Start}|{location.SourceSpan.Length}";
    }

    public static string WithStaleNotice(string output, RoslynWorkspaceService workspace) =>
        workspace.StaleNotice is { } notice ? output + "\n\n" + notice : output;
}
