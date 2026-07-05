using System.ComponentModel;
using System.Text;
using McpRoslyn.Analysis;
using McpRoslyn.Infrastructure;
using McpRoslyn.Symbols;
using McpRoslyn.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpRoslyn.Tools;

[McpServerToolType]
public static class NavigationTools
{
    // ---------------------------------------------------------------- find_references

    [McpServerTool(Name = "find_references")]
    [Description("All references to a symbol, semantically exact: includes overload-correct calls, extension-method uses, aliases and partials; never matches comments/strings. Fields/properties/locals get a read/write flag (r=read, W=write, rw=both, n=nameof). Does NOT see reflection or DI-container lookups.")]
    public static Task<string> FindReferences(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Symbol name, e.g. 'OrderService.Process' or a doc-ID from a previous result.")]
        string symbol,
        [Description("Only references inside projects whose name contains this.")]
        string? project = null,
        [Description("Include the source line of each reference (default true; false saves tokens).")]
        bool include_snippets = true,
        [Description("Page size, 1-200 (default 50).")]
        int max_results = 50,
        [Description("1-based page number.")]
        int page = 1)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "find_references", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
            var target = resolved.Symbol;

            var referencedSymbols = await SymbolFinder.FindReferencesAsync(target, solution, ct);

            // Pass 1 — dedupe, count and sort using only line spans (cheap); snippets and
            // read/write classification are materialized for the rendered page only.
            var seen = new HashSet<string>();
            var hits = new List<(string Path, int Line, ReferenceLocation Location)>();
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var referenced in referencedSymbols)
            {
                foreach (var location in referenced.Locations)
                {
                    ct.ThrowIfCancellationRequested();
                    if (project is not null
                        && !location.Document.Project.Name.Contains(project, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!seen.Add(ToolHelpers.LocationKey(location.Location)))
                        continue;

                    var lineSpan = location.Location.GetLineSpan();
                    files.Add(lineSpan.Path);
                    hits.Add((lineSpan.Path, lineSpan.StartLinePosition.Line + 1, location));
                }
            }

            var fqn = SymbolFormat.FqnOf(target);
            var definedFooter = "defined: " + string.Join(", ", SymbolFormat.DeclarationLocations(target, workspace.RelPath));
            if (hits.Count == 0)
            {
                return ToolHelpers.WithStaleNotice(
                    $"0 references to {fqn} found in the loaded solution "
                    + "(reflection/DI/serialized usages are invisible to static analysis).\n" + definedFooter,
                    workspace);
            }

            hits.Sort((a, b) =>
            {
                var byPath = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
            });

            max_results = Math.Clamp(max_results <= 0 ? Paging.DefaultMax : max_results, 1, Paging.MaxAllowed);
            page = Math.Max(1, page);
            var start = (page - 1) * max_results;
            if (start >= hits.Count)
                return $"{hits.Count} reference(s), but page {page} is past the end.";
            var slice = hits.Skip(start).Take(max_results).ToList();

            // Pass 2 — materialize only the rendered rows.
            var classify = UsageClassifier.IsClassifiable(target);
            var rootCache = new Dictionary<DocumentId, (SyntaxNode? Root, Microsoft.CodeAnalysis.Text.SourceText Text)>();
            var sb = new StringBuilder();
            sb.Append($"{hits.Count} reference(s) to {fqn} in {files.Count} file(s)");
            if (hits.Count > slice.Count)
            {
                sb.Append($" (showing {start + 1}–{start + slice.Count}");
                if (start + slice.Count < hits.Count)
                    sb.Append($"; page={page + 1} for more");
                sb.Append(')');
            }
            sb.AppendLine(":");

            foreach (var (path, line, location) in slice)
            {
                if (!rootCache.TryGetValue(location.Document.Id, out var cached))
                {
                    cached = (await location.Document.GetSyntaxRootAsync(ct),
                              await location.Document.GetTextAsync(ct));
                    rootCache[location.Document.Id] = cached;
                }

                var flag = classify && cached.Root is not null
                    ? UsageClassifier.Classify(cached.Root, location.Location.SourceSpan)
                    : "";
                var implicitTag = location.IsImplicit ? " (implicit)" : "";
                var snippet = include_snippets
                    ? "  " + SymbolFormat.LineSnippet(cached.Text, location.Location.SourceSpan, 120)
                    : "";
                sb.AppendLine($"{workspace.RelPath(path)}:{line}"
                              + (flag.Length > 0 ? $" {flag}" : "") + implicitTag + snippet);
            }

            sb.AppendLine(definedFooter);
            return ToolHelpers.WithStaleNotice(sb.ToString().TrimEnd(), workspace);
        });

    // ---------------------------------------------------------------- find_implementations

    [McpServerTool(Name = "find_implementations")]
    [Description("Who implements/overrides/derives: implementations of an interface or interface member, overrides of a virtual/abstract member, or types derived from a class. 'auto' picks the right relation from the symbol's shape. Lists ALL static candidates — which one a DI container injects at runtime is not knowable statically.")]
    public static Task<string> FindImplementations(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Interface, class, or member name.")]
        string symbol,
        [Description("auto|implementations|overrides|derived (default auto).")]
        string kind = "auto",
        [Description("Page size, 1-200 (default 50).")]
        int max_results = 50,
        [Description("1-based page number.")]
        int page = 1)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "find_implementations", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
            var target = resolved.Symbol;

            var relation = kind.Trim().ToLowerInvariant();
            if (relation is "auto" or "")
            {
                relation = target switch
                {
                    INamedTypeSymbol { TypeKind: TypeKind.Interface } => "implementations",
                    INamedTypeSymbol => "derived",
                    { ContainingType.TypeKind: TypeKind.Interface } => "implementations",
                    { IsVirtual: true } or { IsAbstract: true } or { IsOverride: true } => "overrides",
                    _ => throw new ToolException(
                        $"{SymbolFormat.FqnOf(target)} is a non-virtual {SymbolFormat.KindOf(target)} — nothing implements or overrides it. Use find_references instead."),
                };
            }

            var results = new List<ISymbol>();
            switch (relation)
            {
                case "implementations":
                    results.AddRange(await SymbolFinder.FindImplementationsAsync(target, solution, cancellationToken: ct));
                    if (target is INamedTypeSymbol { TypeKind: TypeKind.Interface } iface)
                        results.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(iface, solution, transitive: true, cancellationToken: ct));
                    break;
                case "overrides":
                    results.AddRange(await SymbolFinder.FindOverridesAsync(target, solution, cancellationToken: ct));
                    break;
                case "derived":
                    if (target is not INamedTypeSymbol type)
                        throw new ToolException("kind=derived needs a class or interface.");
                    if (type.TypeKind == TypeKind.Interface)
                        results.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: ct));
                    else
                        results.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct));
                    break;
                default:
                    throw new ToolException($"Unknown kind '{kind}' — use auto|implementations|overrides|derived.");
            }

            var unique = results
                .GroupBy(SymbolFormat.IdentityKey)
                .Select(g => g.First())
                .OrderBy(SymbolFormat.FqnOf, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var output = Paging.Render(
                unique, page, max_results, $"{relation} of {SymbolFormat.FqnOf(target)}",
                s => $"{SymbolFormat.KindOf(s)} {SymbolFormat.FqnOf(s)} — {SymbolFormat.PrimaryLocation(s, workspace.RelPath)}",
                emptyMessage: $"0 {relation} of {SymbolFormat.FqnOf(target)} in the loaded solution.");

            return ToolHelpers.WithStaleNotice(output, workspace);
        });

    // ---------------------------------------------------------------- get_type_hierarchy

    [McpServerTool(Name = "get_type_hierarchy")]
    [Description("Full type hierarchy of one type: base-class chain and all interfaces upward, derived/implementing types downward.")]
    public static Task<string> GetTypeHierarchy(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Type name.")]
        string symbol,
        [Description("up|down|both (default both).")]
        string direction = "both",
        [Description("Max derived/implementing types listed, 1-200 (default 50).")]
        int max_results = 50)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "get_type_hierarchy", async () =>
        {
            max_results = Math.Clamp(max_results <= 0 ? Paging.DefaultMax : max_results, 1, Paging.MaxAllowed);
            var solution = await workspace.GetSolutionAsync(ct);
            var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
            if (resolved.Symbol is not INamedTypeSymbol type)
                throw new ToolException($"{SymbolFormat.FqnOf(resolved.Symbol)} is a {SymbolFormat.KindOf(resolved.Symbol)}, not a type.");

            var dir = direction.Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            sb.AppendLine($"{SymbolFormat.KindOf(type)} {SymbolFormat.FqnOf(type)} — {SymbolFormat.PrimaryLocation(type, workspace.RelPath)}");

            if (dir is "up" or "both")
            {
                var chain = new List<string>();
                for (var current = type.BaseType; current is not null; current = current.BaseType)
                    chain.Add(current.ToDisplayString(SymbolFormat.Fqn));
                if (chain.Count > 0)
                    sb.AppendLine("base chain: " + string.Join(" ← ", chain));
                if (type.AllInterfaces.Length > 0)
                    sb.AppendLine($"implements ({type.AllInterfaces.Length}): "
                                  + string.Join(", ", type.AllInterfaces.Select(i => i.ToDisplayString(SymbolFormat.Fqn))));
            }

            if (dir is "down" or "both")
            {
                var derived = new List<INamedTypeSymbol>();
                if (type.TypeKind == TypeKind.Interface)
                {
                    derived.AddRange(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: ct));
                    derived.AddRange((await SymbolFinder.FindImplementationsAsync(type, solution, cancellationToken: ct)).OfType<INamedTypeSymbol>());
                }
                else
                {
                    derived.AddRange(await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: ct));
                }

                var unique = derived
                    .GroupBy(SymbolFormat.IdentityKey)
                    .Select(g => g.First())
                    .OrderBy(SymbolFormat.FqnOf, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                sb.AppendLine($"derived/implementing ({unique.Count}{(unique.Count > max_results ? $", showing {max_results}" : "")}):");
                foreach (var d in unique.Take(max_results))
                    sb.AppendLine($"  {SymbolFormat.KindOf(d)} {SymbolFormat.FqnOf(d)} — {SymbolFormat.PrimaryLocation(d, workspace.RelPath)}");
                if (unique.Count == 0)
                    sb.AppendLine("  (none in the loaded solution)");
            }

            return ToolHelpers.WithStaleNotice(sb.ToString().TrimEnd(), workspace);
        });

    // ---------------------------------------------------------------- call_hierarchy

    [McpServerTool(Name = "call_hierarchy")]
    [Description("Callers of (or calls made by) a method/property/constructor, optionally transitive to depth 3. Callers found through interface dispatch are included; delegate/reflection invocations are not visible.")]
    public static Task<string> CallHierarchy(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Method, property or constructor name (e.g. 'OrderService.Process' or 'OrderService.ctor').")]
        string symbol,
        [Description("callers|callees (default callers).")]
        string direction = "callers",
        [Description("Levels to expand, 1-3 (default 1).")]
        int depth = 1,
        [Description("Max entries per level, 1-100 (default 25).")]
        int max_results = 25)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "call_hierarchy", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
            var target = resolved.Symbol;
            depth = Math.Clamp(depth, 1, 3);
            max_results = Math.Clamp(max_results, 1, 100);

            var sb = new StringBuilder();
            var dir = direction.Trim().ToLowerInvariant();
            var budget = 150;
            // Each caller expansion is a solution-wide search; bound wall-clock, not just lines.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

            if (dir is "callers" or "")
            {
                sb.AppendLine($"callers of {SymbolFormat.FqnOf(target)} (depth {depth}):");
                await RenderCallersAsync(sb, target, solution, workspace, depth, 0, max_results, new HashSet<string>(), budget, deadline, ct);
            }
            else if (dir == "callees")
            {
                sb.AppendLine($"callees of {SymbolFormat.FqnOf(target)} (depth {depth}):");
                await RenderCalleesAsync(sb, target, solution, workspace, depth, 0, max_results, new HashSet<string>(), budget, deadline, ct);
            }
            else
            {
                throw new ToolException($"Unknown direction '{direction}' — use callers|callees.");
            }

            return ToolHelpers.WithStaleNotice(sb.ToString().TrimEnd(), workspace);
        });

    private static async Task<int> RenderCallersAsync(
        StringBuilder sb, ISymbol target, Solution solution, RoslynWorkspaceService workspace,
        int remainingDepth, int indentLevel, int maxPerLevel, HashSet<string> visited, int budget, DateTime deadline, CancellationToken ct)
    {
        var callers = (await SymbolFinder.FindCallersAsync(target, solution, ct))
            .GroupBy(c => c.CallingSymbol.GetDocumentationCommentId() ?? SymbolFormat.FqnOf(c.CallingSymbol))
            .Select(g => (Symbol: g.First().CallingSymbol,
                          Locations: g.SelectMany(x => x.Locations).Where(l => l.IsInSource).ToList(),
                          IsDirect: g.Any(x => x.IsDirect)))
            .OrderBy(c => SymbolFormat.FqnOf(c.Symbol), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var indent = new string(' ', indentLevel * 2);
        if (callers.Count == 0)
        {
            sb.AppendLine($"{indent}(no callers found in the loaded solution)");
            return budget;
        }

        foreach (var caller in callers.Take(maxPerLevel))
        {
            if (budget-- <= 0)
            {
                sb.AppendLine($"{indent}… output budget reached, narrow with a smaller depth.");
                return 0;
            }

            var sites = string.Join(", ", caller.Locations.Take(4)
                .Select(l => SymbolFormat.Location(l, workspace.RelPath)));
            if (caller.Locations.Count > 4)
                sites += $" +{caller.Locations.Count - 4} more";
            var viaTag = caller.IsDirect ? "" : " (indirect/interface)";

            sb.AppendLine($"{indent}{SymbolFormat.KindOf(caller.Symbol)} {SymbolFormat.FqnOf(caller.Symbol)}{viaTag} — {sites}");

            var key = caller.Symbol.GetDocumentationCommentId() ?? SymbolFormat.FqnOf(caller.Symbol);
            if (remainingDepth > 1 && visited.Add(key))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    sb.AppendLine($"{indent}… time budget reached — narrow with a smaller depth or max_results.");
                    return 0;
                }
                budget = await RenderCallersAsync(sb, caller.Symbol, solution, workspace,
                    remainingDepth - 1, indentLevel + 1, maxPerLevel, visited, budget, deadline, ct);
            }
        }

        if (callers.Count > maxPerLevel)
            sb.AppendLine($"{indent}… {callers.Count - maxPerLevel} more callers at this level (raise max_results).");
        return budget;
    }

    private static async Task<int> RenderCalleesAsync(
        StringBuilder sb, ISymbol target, Solution solution, RoslynWorkspaceService workspace,
        int remainingDepth, int indentLevel, int maxPerLevel, HashSet<string> visited, int budget, DateTime deadline, CancellationToken ct)
    {
        var callees = await CollectCalleesAsync(target, solution, ct);
        var indent = new string(' ', indentLevel * 2);

        if (callees.Count == 0)
        {
            sb.AppendLine($"{indent}(no outgoing calls — symbol may be abstract, external, or bodiless)");
            return budget;
        }

        foreach (var callee in callees.Take(maxPerLevel))
        {
            if (budget-- <= 0)
            {
                sb.AppendLine($"{indent}… output budget reached.");
                return 0;
            }

            var external = callee.Locations.Any(l => l.IsInSource)
                ? SymbolFormat.PrimaryLocation(callee, workspace.RelPath)
                : $"[{callee.ContainingAssembly?.Name}]";
            sb.AppendLine($"{indent}{SymbolFormat.KindOf(callee)} {SymbolFormat.FqnOf(callee)} — {external}");

            var key = callee.GetDocumentationCommentId() ?? SymbolFormat.FqnOf(callee);
            if (remainingDepth > 1 && callee.Locations.Any(l => l.IsInSource) && visited.Add(key))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    sb.AppendLine($"{indent}… time budget reached.");
                    return 0;
                }
                budget = await RenderCalleesAsync(sb, callee, solution, workspace,
                    remainingDepth - 1, indentLevel + 1, maxPerLevel, visited, budget, deadline, ct);
            }
        }

        if (callees.Count > maxPerLevel)
            sb.AppendLine($"{indent}… {callees.Count - maxPerLevel} more callees at this level.");
        return budget;
    }

    private static async Task<List<ISymbol>> CollectCalleesAsync(ISymbol symbol, Solution solution, CancellationToken ct)
    {
        var callees = new List<ISymbol>();
        var seen = new HashSet<string>();

        foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
        {
            var node = await syntaxRef.GetSyntaxAsync(ct);
            var document = solution.GetDocument(node.SyntaxTree);
            if (document is null)
                continue;
            var semanticModel = await document.GetSemanticModelAsync(ct);
            if (semanticModel is null)
                continue;

            // VB declares methods via the statement; the body lives in the parent block.
            var operation = semanticModel.GetOperation(node, ct) ?? (node.Parent is { } parent ? semanticModel.GetOperation(parent, ct) : null);
            if (operation is null)
                continue;

            foreach (var op in DescendantOperations(operation))
            {
                ISymbol? callee = op switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod,
                    IObjectCreationOperation creation => creation.Constructor,
                    IPropertyReferenceOperation property => property.Property,
                    _ => null,
                };
                if (callee is null)
                    continue;
                callee = callee.OriginalDefinition;
                if (seen.Add(callee.GetDocumentationCommentId() ?? SymbolFormat.FqnOf(callee)))
                    callees.Add(callee);
            }
        }

        return callees
            .OrderByDescending(c => c.Locations.Any(l => l.IsInSource))
            .ThenBy(SymbolFormat.FqnOf, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<IOperation> DescendantOperations(IOperation root)
    {
        var stack = new Stack<IOperation>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.ChildOperations)
                stack.Push(child);
        }
    }

    // ---------------------------------------------------------------- get_usage_examples

    [McpServerTool(Name = "get_usage_examples")]
    [Description("Real call-site examples for an API, extracted from the loaded solution and picked for diversity (different files/projects). The fastest way to learn how a method is actually called before using it yourself.")]
    public static Task<string> GetUsageExamples(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Method/property/type name.")]
        string symbol,
        [Description("How many examples (default 3, max 10).")]
        int max_examples = 3)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "get_usage_examples", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
            var target = resolved.Symbol;
            max_examples = Math.Clamp(max_examples, 1, 10);

            var references = await SymbolFinder.FindReferencesAsync(target, solution, ct);
            var locations = references
                .SelectMany(r => r.Locations)
                .Where(l => !l.IsImplicit)
                .GroupBy(l => ToolHelpers.LocationKey(l.Location))
                .Select(g => g.First())
                .ToList();

            if (locations.Count == 0)
                return $"0 usages of {SymbolFormat.FqnOf(target)} in the loaded solution — no examples available.";

            // Round-robin across files for diversity.
            var byFile = locations
                .GroupBy(l => l.Document.FilePath ?? l.Document.Name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Key.Contains("test", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new Queue<ReferenceLocation>(g))
                .ToList();

            var picked = new List<ReferenceLocation>();
            while (picked.Count < max_examples && byFile.Any(q => q.Count > 0))
            {
                foreach (var queue in byFile)
                {
                    if (picked.Count >= max_examples)
                        break;
                    if (queue.Count > 0)
                        picked.Add(queue.Dequeue());
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{locations.Count} usage(s) of {SymbolFormat.FqnOf(target)}; {picked.Count} example(s):");

            foreach (var location in picked)
            {
                var document = location.Document;
                var root = await document.GetSyntaxRootAsync(ct);
                var text = await document.GetTextAsync(ct);
                var semanticModel = await document.GetSemanticModelAsync(ct);
                if (root is null)
                    continue;

                var lineSpan = location.Location.GetLineSpan();
                var enclosing = semanticModel?.GetEnclosingSymbol(location.Location.SourceSpan.Start, ct);
                var enclosingText = enclosing is null ? "" : $" (in {SymbolFormat.FqnOf(enclosing)})";
                sb.AppendLine();
                sb.AppendLine($"— {workspace.RelPath(lineSpan.Path)}:{lineSpan.StartLinePosition.Line + 1}{enclosingText}");

                var snippet = ExtractStatement(root, location.Location.SourceSpan, text);
                foreach (var line in snippet)
                    sb.AppendLine($"    {line}");
            }

            return ToolHelpers.WithStaleNotice(sb.ToString().TrimEnd(), workspace);
        });

    private static IReadOnlyList<string> ExtractStatement(
        SyntaxNode root, Microsoft.CodeAnalysis.Text.TextSpan span, Microsoft.CodeAnalysis.Text.SourceText text)
    {
        var node = root.FindNode(span);
        SyntaxNode? statement = null;
        for (var current = node; current is not null; current = current.Parent)
        {
            if (current is Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax
                or Microsoft.CodeAnalysis.VisualBasic.Syntax.StatementSyntax)
            {
                statement = current;
                break;
            }
        }

        var snippetText = (statement ?? node).ToString();
        var lines = snippetText.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .ToList();

        if (lines.Count > 8)
        {
            lines = lines.Take(8).ToList();
            lines.Add("…");
        }

        // Un-indent uniformly.
        var minIndent = lines.Where(l => l.Trim().Length > 0)
            .Select(l => l.Length - l.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();
        return lines.Select(l => l.Length >= minIndent ? l[minIndent..] : l).ToList();
    }
}
