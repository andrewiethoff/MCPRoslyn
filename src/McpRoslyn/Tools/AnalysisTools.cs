using System.Collections.Immutable;
using System.ComponentModel;
using System.Text;
using System.Xml.Linq;
using McpRoslyn.Infrastructure;
using McpRoslyn.Symbols;
using McpRoslyn.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpRoslyn.Tools;

[McpServerToolType]
public static class AnalysisTools
{
    // ---------------------------------------------------------------- get_diagnostics

    [McpServerTool(Name = "get_diagnostics")]
    [Description("Compiler diagnostics from the warm workspace — orders of magnitude faster than 'dotnet build' for a quick post-edit check. Scope via target: omit for the whole solution, pass a file path for one file, or a project name for one project. Analyzer rules (CAxxxx/IDExxxx) only with include_analyzers=true (slower). Note: not a full build — a final 'dotnet build' before committing is still advised.")]
    public static Task<string> GetDiagnostics(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Omit = whole solution; a .cs/.vb path = that file; anything else = project name.")]
        string? target = null,
        [Description("error|warning|info|hidden (default warning).")]
        string? min_severity = null,
        [Description("Also run the project's Roslyn analyzers (CA/IDE rules). Slower, and OUTSIDE the read-only guarantee: this loads and executes the third-party analyzer assemblies the project references in-process, which is trusted-code execution. Off by default. File scope runs tree-scoped analysis (skips whole-project rules).")]
        bool include_analyzers = false,
        [Description("Page size, 1-200 (default 50).")]
        int max_results = 50,
        [Description("1-based page number.")]
        int page = 1)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "get_diagnostics", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var minSeverity = ToolHelpers.ParseMinSeverity(min_severity);

            List<Diagnostic> diagnostics = [];
            string scopeDescription;

            if (target is { Length: > 0 } &&
                (target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || target.EndsWith(".vb", StringComparison.OrdinalIgnoreCase)))
            {
                var document = ToolHelpers.FindDocument(solution, workspace, target);
                var semanticModel = await document.GetSemanticModelAsync(ct)
                    ?? throw new ToolException("No semantic model for this file.");
                diagnostics.AddRange(semanticModel.GetDiagnostics(cancellationToken: ct));
                scopeDescription = workspace.RelPath(document.FilePath);
                if (include_analyzers)
                    diagnostics.AddRange(await DocumentAnalyzerDiagnosticsAsync(document, semanticModel, ct));
            }
            else if (target is { Length: > 0 })
            {
                var project = ToolHelpers.FindProject(solution, target);
                // Check every target framework of the project — a diagnostic can be TFM-specific
                // (behind #if guards or per-TFM APIs). Duplicates across TFMs collapse downstream.
                var flavors = AllTfmFlavors(solution, project);
                foreach (var flavor in flavors)
                    diagnostics.AddRange(await ProjectDiagnosticsAsync(flavor, include_analyzers, minSeverity, ct));
                scopeDescription = flavors.Count > 1
                    ? $"project {StripTfm(project.Name)} (all {flavors.Count} target frameworks)"
                    : $"project {project.Name}";
            }
            else
            {
                // Every project flavor, all TFMs — TFM-specific diagnostics only surface under
                // their own framework. The DiagnosticKey dedupe below collapses cross-TFM copies.
                var projects = solution.Projects.ToList();
                var distinctProjects = projects
                    .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase).Count();
                var semaphore = new SemaphoreSlim(4);
                var tasks = projects.Select(async p =>
                {
                    await semaphore.WaitAsync(ct);
                    try
                    {
                        return await ProjectDiagnosticsAsync(p, include_analyzers, minSeverity, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });
                foreach (var set in await Task.WhenAll(tasks))
                    diagnostics.AddRange(set);
                scopeDescription = $"solution ({distinctProjects} project(s), all target frameworks)";
            }

            var rows = diagnostics
                .Where(d => d.Severity >= minSeverity && !d.IsSuppressed)
                .GroupBy(DiagnosticKey)
                .Select(g => g.First())
                .OrderByDescending(d => d.Severity)
                .ThenBy(d => d.Location.SourceTree?.FilePath ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Location.SourceSpan.Start)
                .ToList();

            var output = Paging.Render(
                rows, page, max_results, $"diagnostic(s) ≥{minSeverity.ToString().ToLowerInvariant()} in {scopeDescription}",
                d => RenderDiagnostic(d, workspace),
                emptyMessage: $"0 diagnostics ≥{minSeverity.ToString().ToLowerInvariant()} in {scopeDescription}. ✔");

            return ToolHelpers.WithStaleNotice(output, workspace);
        });

    private static async Task<List<Diagnostic>> ProjectDiagnosticsAsync(
        Project project, bool includeAnalyzers, DiagnosticSeverity minSeverity, CancellationToken ct)
    {
        // Filter before returning so a solution-wide sweep never retains the (potentially huge)
        // hidden/info analyzer output of every project at once.
        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return [];

        if (!includeAnalyzers)
            return compilation.GetDiagnostics(ct).Where(d => d.Severity >= minSeverity).ToList();

        var analyzers = project.AnalyzerReferences
            .SelectMany(r => r.GetAnalyzers(project.Language))
            .ToImmutableArray();
        if (analyzers.IsEmpty)
            return compilation.GetDiagnostics(ct).Where(d => d.Severity >= minSeverity).ToList();

        var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
        return (await withAnalyzers.GetAllDiagnosticsAsync(ct)).Where(d => d.Severity >= minSeverity).ToList();
    }

    /// <summary>
    /// Tree-scoped analyzer run for one document — avoids executing the analyzers over the whole
    /// project for a one-file question. Skips compilation-end (whole-project) rules by design.
    /// </summary>
    private static async Task<List<Diagnostic>> DocumentAnalyzerDiagnosticsAsync(
        Document document, SemanticModel semanticModel, CancellationToken ct)
    {
        var project = document.Project;
        var analyzers = project.AnalyzerReferences
            .SelectMany(r => r.GetAnalyzers(project.Language))
            .ToImmutableArray();
        if (analyzers.IsEmpty)
            return [];

        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return [];

        var withAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
        var result = new List<Diagnostic>();
        result.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(semanticModel.SyntaxTree, ct));
        result.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(semanticModel, filterSpan: null, ct));
        return result;
    }

    private static string DiagnosticKey(Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        return $"{d.Id}|{span.Path}|{span.StartLinePosition.Line}|{d.GetMessage()}";
    }

    /// <summary>
    /// Line-insensitive identity for a diagnostic, used to diff two compilations. Excluding the
    /// line number means a benign edit that only shifts subsequent lines (inserting a blank line,
    /// say) is not misreported as resolving the old diagnostic and introducing an identical new one.
    /// </summary>
    private static string DiagnosticMatchKey(Diagnostic d) =>
        $"{d.Id}|{d.Location.GetLineSpan().Path}|{d.GetMessage()}";

    /// <summary>
    /// Folds one compilation's diagnostics into a per-key running MAX count (max across the TFM
    /// flavors already folded in), and, when <paramref name="samples"/> is given, records the actual
    /// diagnostic objects per key so introduced ones can be rendered with their real locations.
    /// </summary>
    private static void MergeMaxCounts(
        Dictionary<string, int> maxCounts, List<Diagnostic> diagnostics, Dictionary<string, List<Diagnostic>>? samples)
    {
        var counts = new Dictionary<string, int>();
        foreach (var d in diagnostics)
        {
            var key = DiagnosticMatchKey(d);
            counts[key] = counts.GetValueOrDefault(key) + 1;
            if (samples is not null)
            {
                if (!samples.TryGetValue(key, out var list))
                    samples[key] = list = [];
                list.Add(d);
            }
        }
        foreach (var (key, count) in counts)
            maxCounts[key] = Math.Max(maxCounts.GetValueOrDefault(key), count);
    }

    /// <summary>All target-framework flavors of a project (identified by shared project file path).</summary>
    private static List<Project> AllTfmFlavors(Solution solution, Project project) =>
        project.FilePath is null
            ? [project]
            : solution.Projects
                .Where(p => string.Equals(p.FilePath, project.FilePath, StringComparison.OrdinalIgnoreCase))
                .ToList();

    private static string RenderDiagnostic(Diagnostic d, RoslynWorkspaceService workspace)
    {
        var message = d.GetMessage();
        if (message.Length > 240)
            message = message[..240] + "…";
        var where = d.Location.IsInSource
            ? $"{workspace.RelPath(d.Location.GetLineSpan().Path)}:{d.Location.GetLineSpan().StartLinePosition.Line + 1}"
            : "(project)";
        return $"{ToolHelpers.SeverityShort(d.Severity)} {d.Id} {where} — {message}";
    }

    // ---------------------------------------------------------------- get_project_graph

    [McpServerTool(Name = "get_project_graph")]
    [Description("Solution overview: every project with target frameworks, language, output kind, file count, project references, and (optionally) NuGet packages. The one-call orientation tool for an unfamiliar solution.")]
    public static Task<string> GetProjectGraph(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Include NuGet package references per project (default true).")]
        bool include_packages = true,
        [Description("Projects per page, 1-200 (default 50).")]
        int max_results = 50,
        [Description("1-based page number.")]
        int page = 1)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "get_project_graph", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var groups = solution.Projects
                .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var output = Paging.Render(
                groups, page, max_results, $"project(s) in {workspace.SolutionPath}",
                group =>
                {
                    var project = group.First();
                    var tfms = group.Select(p => TfmSuffix(p.Name)).Where(t => t is not null).Distinct().ToList();
                    var outputKind = project.CompilationOptions?.OutputKind switch
                    {
                        OutputKind.ConsoleApplication or OutputKind.WindowsApplication => "exe",
                        OutputKind.DynamicallyLinkedLibrary => "dll",
                        var k => k?.ToString().ToLowerInvariant() ?? "?",
                    };
                    var references = group
                        .SelectMany(p => p.ProjectReferences)
                        .Select(r => solution.GetProject(r.ProjectId)?.Name)
                        .Where(n => n is not null)
                        .Select(n => StripTfm(n!))
                        .Distinct()
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var line = $"{StripTfm(project.Name)}{(tfms.Count > 0 ? $" [{string.Join(";", tfms)}]" : "")}"
                               + $" {(project.Language == "Visual Basic" ? "VB" : project.Language)} {outputKind} — {project.DocumentIds.Count} files";
                    if (references.Count > 0)
                    {
                        line += " → refs: " + string.Join(", ", references.Take(20));
                        if (references.Count > 20)
                            line += $" +{references.Count - 20} more";
                    }

                    if (include_packages && project.FilePath is not null)
                    {
                        var packages = ReadPackages(project.FilePath);
                        if (packages.Count > 0)
                            line += "\n  packages: " + string.Join(", ", packages.Take(40));
                    }
                    return line;
                });

            return ToolHelpers.WithStaleNotice(output, workspace);
        });

    private static string? TfmSuffix(string projectName)
    {
        var open = projectName.LastIndexOf('(');
        return open >= 0 && projectName.EndsWith(")", StringComparison.Ordinal)
            ? projectName[(open + 1)..^1]
            : null;
    }

    private static string StripTfm(string projectName)
    {
        var open = projectName.LastIndexOf('(');
        return open > 0 && projectName.EndsWith(")", StringComparison.Ordinal)
            ? projectName[..open].TrimEnd()
            : projectName;
    }

    private static List<string> ReadPackages(string projectFilePath)
    {
        var packages = new List<string>();
        try
        {
            var doc = XDocument.Load(projectFilePath);
            foreach (var reference in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var id = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value;
                if (id is null)
                    continue;
                var version = reference.Attribute("Version")?.Value
                              ?? reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
                packages.Add(version is null ? id : $"{id} {version}");
            }

            var packagesConfig = Path.Combine(Path.GetDirectoryName(projectFilePath)!, "packages.config");
            if (packages.Count == 0 && File.Exists(packagesConfig))
            {
                var config = XDocument.Load(packagesConfig);
                foreach (var package in config.Descendants().Where(e => e.Name.LocalName == "package"))
                {
                    var id = package.Attribute("id")?.Value;
                    if (id is not null)
                        packages.Add($"{id} {package.Attribute("version")?.Value}".TrimEnd());
                }
            }
        }
        catch
        {
            // Package info is best-effort.
        }
        return packages;
    }

    // ---------------------------------------------------------------- find_unused

    [McpServerTool(Name = "find_unused")]
    [Description("Symbols with zero references in the loaded solution. By default only checks private/internal symbols and skips overrides, interface implementations and attributed symbols to keep false positives low. CAVEAT: usage via reflection, DI registration-by-convention, serialization or designer/XAML wiring is invisible — treat results as candidates, not verdicts.")]
    public static Task<string> FindUnused(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Scope: a project name, a namespace, or a type name. Omit to scan the whole solution (capped).")]
        string? target = null,
        [Description("Also check public/protected symbols (more false positives if this solution is consumed elsewhere).")]
        bool include_public = false,
        [Description("Page size, 1-200 (default 50).")]
        int max_results = 50,
        [Description("1-based page number.")]
        int page = 1)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "find_unused", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            const int maxChecked = 300;

            var candidates = new List<ISymbol>();
            string scopeDescription;

            if (target is null or "")
            {
                // Stop collecting once the cap is exceeded — don't force compilations of projects
                // whose candidates would be discarded anyway. Report which projects were covered.
                var projects = SymbolResolver.DistinctByProjectFile(solution).ToList();
                var covered = new List<string>();
                foreach (var project in projects)
                {
                    if (candidates.Count > maxChecked)
                        break;
                    candidates.AddRange(await CollectCandidatesAsync(project, include_public, ct));
                    covered.Add(StripTfm(project.Name));
                }
                scopeDescription = covered.Count < projects.Count
                    ? $"solution (cap reached after {covered.Count} of {projects.Count} projects: {string.Join(", ", covered)} — pass a project name to scan the rest)"
                    : "solution";
            }
            else if (solution.Projects.Any(p => p.Name.Contains(target, StringComparison.OrdinalIgnoreCase)))
            {
                var project = ToolHelpers.FindProject(solution, target);
                candidates.AddRange(await CollectCandidatesAsync(project, include_public, ct));
                scopeDescription = $"project {project.Name}";
            }
            else
            {
                var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, target, ct);
                switch (resolved.Symbol)
                {
                    case INamedTypeSymbol type:
                        candidates.AddRange(FilterCandidates(type.GetMembers(), include_public));
                        scopeDescription = $"type {SymbolFormat.FqnOf(type)}";
                        break;
                    case INamespaceSymbol ns:
                        foreach (var type in AllTypes(ns).Where(t => t.Locations.Any(l => l.IsInSource)))
                        {
                            if (include_public || type.DeclaredAccessibility != Accessibility.Public)
                                candidates.Add(type);
                            candidates.AddRange(FilterCandidates(type.GetMembers(), include_public));
                        }
                        scopeDescription = $"namespace {target}";
                        break;
                    default:
                        throw new ToolException($"'{target}' is a {SymbolFormat.KindOf(resolved.Symbol)} — pass a project, namespace or type.");
                }
            }

            var truncatedNote = candidates.Count > maxChecked
                ? $" (checked the first {maxChecked} of {candidates.Count} candidates — narrow the scope for full coverage)"
                : "";
            var unused = new List<ISymbol>();

            foreach (var candidate in candidates.Take(maxChecked))
            {
                ct.ThrowIfCancellationRequested();
                var references = await SymbolFinder.FindReferencesAsync(candidate, solution, ct);
                var declarationSpans = candidate.DeclaringSyntaxReferences
                    .Select(r => (r.SyntaxTree.FilePath, r.Span))
                    .ToList();

                var used = references
                    .SelectMany(r => r.Locations)
                    .Any(l => !declarationSpans.Any(d =>
                        string.Equals(d.FilePath, l.Location.SourceTree?.FilePath, StringComparison.OrdinalIgnoreCase)
                        && d.Span.Contains(l.Location.SourceSpan)));

                if (!used)
                    unused.Add(candidate);
            }

            var output = Paging.Render(
                unused, page, max_results, $"unreferenced symbol(s) in {scopeDescription}{truncatedNote}",
                s => $"{SymbolFormat.KindOf(s)} {SymbolFormat.FqnOf(s)} — {SymbolFormat.PrimaryLocation(s, workspace.RelPath)}",
                emptyMessage: $"0 unreferenced symbols in {scopeDescription}{truncatedNote}.",
                footer: "caveat: reflection/DI/serialization/designer usage is invisible to static analysis — verify before deleting.");

            return ToolHelpers.WithStaleNotice(output, workspace);
        });

    private static async Task<List<ISymbol>> CollectCandidatesAsync(Project project, bool includePublic, CancellationToken ct)
    {
        var compilation = await project.GetCompilationAsync(ct);
        if (compilation is null)
            return [];

        var result = new List<ISymbol>();
        foreach (var type in AllTypes(compilation.Assembly.GlobalNamespace))
        {
            ct.ThrowIfCancellationRequested();
            if (!type.Locations.Any(l => l.IsInSource))
                continue;
            if (includePublic || type.DeclaredAccessibility is not Accessibility.Public)
                if (type.GetAttributes().Length == 0)
                    result.Add(type);
            result.AddRange(FilterCandidates(type.GetMembers(), includePublic));
        }
        return result;
    }

    private static IEnumerable<ISymbol> FilterCandidates(IEnumerable<ISymbol> members, bool includePublic) =>
        members.Where(m =>
            !m.IsImplicitlyDeclared
            && m.Locations.Any(l => l.IsInSource)
            && m is not INamedTypeSymbol
            && !m.IsOverride
            && m.GetAttributes().Length == 0
            && (includePublic || m.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal)
            && m switch
            {
                IMethodSymbol method => method.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                                        && method.Name != "Main"
                                        && !IsInterfaceImplementation(method),
                IPropertySymbol property => !IsInterfaceImplementation(property),
                IFieldSymbol field => field.ContainingType.TypeKind != TypeKind.Enum,
                IEventSymbol evt => !IsInterfaceImplementation(evt),
                _ => false,
            });

    private static bool IsInterfaceImplementation(ISymbol member)
    {
        if (member is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
            or IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
            or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
        {
            return true;
        }

        foreach (var iface in member.ContainingType.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers())
            {
                var implementation = member.ContainingType.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation is not null && SymbolEqualityComparer.Default.Equals(implementation, member))
                    return true;
            }
        }
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol root)
    {
        foreach (var member in root.GetMembers())
        {
            switch (member)
            {
                case INamespaceSymbol ns:
                    foreach (var nested in AllTypes(ns))
                        yield return nested;
                    break;
                case INamedTypeSymbol type:
                    yield return type;
                    foreach (var nested in NestedTypes(type))
                        yield return nested;
                    break;
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> NestedTypes(INamedTypeSymbol type)
    {
        foreach (var nested in type.GetTypeMembers())
        {
            yield return nested;
            foreach (var deeper in NestedTypes(nested))
                yield return deeper;
        }
    }

    // ---------------------------------------------------------------- analyze_impact

    [McpServerTool(Name = "analyze_impact")]
    [Description("What breaks if I change this? Two modes. (1) file + new_content: applies the edit IN MEMORY ONLY (disk is never touched), recompiles the project and everything depending on it, and reports the diagnostics delta — new errors/warnings the change would introduce. (2) symbol only: blast radius — where the symbol is referenced/implemented/overridden, grouped by project.")]
    public static Task<string> AnalyzeImpact(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Mode 1: file to virtually edit.")]
        string? file = null,
        [Description("Mode 1: the complete new content of that file.")]
        string? new_content = null,
        [Description("Mode 2: symbol whose blast radius to report.")]
        string? symbol = null)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "analyze_impact", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);

            if (symbol is { Length: > 0 })
                return ToolHelpers.WithStaleNotice(await BlastRadiusAsync(solution, workspace, symbol, ct), workspace);

            if (file is null || new_content is null)
                throw new ToolException("Pass either symbol (blast radius) or file + new_content (speculative edit).");

            return ToolHelpers.WithStaleNotice(
                await SpeculativeEditAsync(solution, workspace, file, new_content, ct), workspace);
        });

    private static async Task<string> BlastRadiusAsync(
        Solution solution, RoslynWorkspaceService workspace, string symbolName, CancellationToken ct)
    {
        var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbolName, ct);
        var target = resolved.Symbol;

        var references = await SymbolFinder.FindReferencesAsync(target, solution, ct);
        var locations = references.SelectMany(r => r.Locations)
            .GroupBy(l => ToolHelpers.LocationKey(l.Location))
            .Select(g => g.First())
            .ToList();

        var byProject = locations
            .GroupBy(l => StripTfm(l.Document.Project.Name))
            .OrderByDescending(g => g.Count())
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"blast radius of {SymbolFormat.KindOf(target)} {SymbolFormat.FqnOf(target)}:");
        sb.AppendLine($"references: {locations.Count} in {byProject.Count} project(s)"
                      + (byProject.Count > 0
                          ? " — " + string.Join(", ", byProject.Take(10).Select(g => $"{g.Key}: {g.Count()}"))
                          : ""));

        if (target is INamedTypeSymbol type)
        {
            var derived = type.TypeKind == TypeKind.Interface
                ? (await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: ct)).Count()
                : (await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct)).Count();
            sb.AppendLine($"{(type.TypeKind == TypeKind.Interface ? "implementations" : "derived classes")}: {derived}");
        }
        else if (target.IsVirtual || target.IsAbstract || target.ContainingType?.TypeKind == TypeKind.Interface)
        {
            var impls = target.ContainingType?.TypeKind == TypeKind.Interface
                ? (await SymbolFinder.FindImplementationsAsync(target, solution, cancellationToken: ct)).Count()
                : (await SymbolFinder.FindOverridesAsync(target, solution, cancellationToken: ct)).Count();
            sb.AppendLine($"implementations/overrides: {impls} (each needs a matching change if the signature changes)");
        }

        if (target is IMethodSymbol or IPropertySymbol)
        {
            // Derive the caller count from the references we already fetched instead of paying a
            // second whole-solution search (FindCallersAsync re-runs FindReferencesAsync inside).
            var callers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var location in locations)
            {
                var model = await location.Document.GetSemanticModelAsync(ct);
                var enclosing = model?.GetEnclosingSymbol(location.Location.SourceSpan.Start, ct);
                if (enclosing is not null)
                    callers.Add(enclosing);
            }
            sb.AppendLine($"calling members: {callers.Count}");
        }

        var files = locations.Select(l => l.Document.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(15).ToList();
        if (files.Count > 0)
            sb.AppendLine("files touched: " + string.Join(", ", files.Select(f => workspace.RelPath(f))));

        sb.AppendLine("tip: for a concrete diagnostics delta, call analyze_impact with file + new_content.");
        return sb.ToString().TrimEnd();
    }

    private static async Task<string> SpeculativeEditAsync(
        Solution solution, RoslynWorkspaceService workspace, string file, string newContent, CancellationToken ct)
    {
        var documents = ToolHelpers.FindAllDocuments(solution, workspace, file);
        var newSolution = solution;
        foreach (var document in documents)
            newSolution = newSolution.WithDocumentText(document.Id, SourceText.From(newContent));

        // Affected = the containing project(s) + everything transitively depending on them.
        var graph = solution.GetProjectDependencyGraph();
        var affectedIds = new HashSet<ProjectId>();
        foreach (var document in documents)
        {
            affectedIds.Add(document.Project.Id);
            foreach (var dependent in graph.GetProjectsThatTransitivelyDependOnThisProject(document.Project.Id))
                affectedIds.Add(dependent);
        }

        // Group by logical project (file), but compile EVERY TFM flavor — a break can exist only
        // under a non-first target framework. Results are merged per logical project.
        var affectedByProject = affectedIds
            .Select(id => solution.GetProject(id)!)
            .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.First().Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        const int projectCap = 20;
        var capNote = affectedByProject.Count > projectCap
            ? $" (limited to the first {projectCap} of {affectedByProject.Count} affected projects)"
            : "";

        var sb = new StringBuilder();
        sb.AppendLine($"speculative edit of {workspace.RelPath(documents[0].FilePath)} — "
                      + $"{affectedByProject.Count} affected project(s){capNote}. In-memory only; no files changed.");

        var totalNew = 0;
        var totalFixed = 0;

        foreach (var projectGroup in affectedByProject.Take(projectCap))
        {
            ct.ThrowIfCancellationRequested();

            // Diff old vs new per TFM flavor with MULTISET counts keyed line-insensitively, taking
            // the MAX count across flavors per key. Line-insensitive so a benign line shift is not a
            // break; multiset so a genuinely-added duplicate (id+message identical to a pre-existing
            // one) is still counted; MAX-across-flavors so a diagnostic every framework emits is not
            // multiplied, and one that only some frameworks lose is only "resolved" once the whole
            // build loses it.
            var oldMax = new Dictionary<string, int>();
            var newMax = new Dictionary<string, int>();
            var newSamples = new Dictionary<string, List<Diagnostic>>();

            foreach (var project in projectGroup)
            {
                var oldCompilation = await project.GetCompilationAsync(ct);
                var newProject = newSolution.GetProject(project.Id);
                var newCompilation = newProject is null ? null : await newProject.GetCompilationAsync(ct);
                if (oldCompilation is null || newCompilation is null)
                    continue;

                MergeMaxCounts(oldMax, Significant(oldCompilation.GetDiagnostics(ct)), null);
                MergeMaxCounts(newMax, Significant(newCompilation.GetDiagnostics(ct)), newSamples);
            }

            var introduced = new List<Diagnostic>();
            var resolved = 0;
            foreach (var key in newMax.Keys.Union(oldMax.Keys))
            {
                var delta = newMax.GetValueOrDefault(key) - oldMax.GetValueOrDefault(key);
                if (delta > 0 && newSamples.TryGetValue(key, out var samples))
                    introduced.AddRange(samples.Take(delta));
                else if (delta < 0)
                    resolved += -delta;
            }
            totalNew += introduced.Count;
            totalFixed += resolved;

            var name = StripTfm(projectGroup.First().Name);
            if (introduced.Count > 0)
            {
                sb.AppendLine($"{name}: +{introduced.Count} new, -{resolved} resolved:");
                foreach (var diagnostic in introduced.Take(30))
                    sb.AppendLine("  " + RenderDiagnostic(diagnostic, workspace));
                if (introduced.Count > 30)
                    sb.AppendLine($"  … {introduced.Count - 30} more");
            }
            else if (resolved > 0)
            {
                sb.AppendLine($"{name}: no new problems, -{resolved} resolved.");
            }
        }

        sb.AppendLine(totalNew == 0
            ? $"verdict: ✔ change introduces no new errors/warnings{(totalFixed > 0 ? $" and resolves {totalFixed}" : "")}."
            : $"verdict: ✖ change would introduce {totalNew} new diagnostic(s) (resolves {totalFixed}).");

        return sb.ToString().TrimEnd();
    }

    private static List<Diagnostic> Significant(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Where(d => d.Severity >= DiagnosticSeverity.Warning && !d.IsSuppressed).ToList();
}
