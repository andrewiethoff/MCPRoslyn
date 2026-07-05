using System.ComponentModel;
using System.Text;
using McpRoslyn.Infrastructure;
using McpRoslyn.Symbols;
using McpRoslyn.Workspace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpRoslyn.Tools;

[McpServerToolType]
public static class SearchTools
{
    [McpServerTool(Name = "search_symbols")]
    [Description("Search declarations in the loaded solution by name: substring and camel-hump patterns work ('OrdSvc' finds OrderService). Returns kind, fully-qualified name and location per match. Semantically exact — unlike grep this only returns real declarations, never comments/strings/usages.")]
    public static Task<string> SearchSymbols(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Name or pattern, e.g. 'OrderService', 'Process', 'OrdSvc' (camel-hump).")]
        string query,
        [Description("Filter: class|interface|struct|enum|record|delegate|method|property|field|event|namespace. Omit for all.")]
        string? kind = null,
        [Description("Only symbols declared in projects whose name contains this.")]
        string? project = null,
        int max_results = 50,
        int page = 1)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "search_symbols", async () =>
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ToolException("Empty query. Pass a symbol name or camel-hump pattern.");

            var solution = await workspace.GetSolutionAsync(ct);
            var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution, query, SymbolFilter.All, ct);

            var kindFilter = kind?.Trim().ToLowerInvariant();
            var results = found
                .Where(s => s.Kind is not SymbolKind.Alias and not SymbolKind.Local and not SymbolKind.Parameter)
                .Where(s => kindFilter is null or "" || MatchesKind(s, kindFilter))
                .Where(s => project is null || ToolHelpers.ProjectOf(solution, s)?.Name.Contains(project, StringComparison.OrdinalIgnoreCase) == true)
                .GroupBy(s => s.GetDocumentationCommentId() ?? SymbolFormat.FqnOf(s) + s.Kind)
                .Select(g => g.First())
                .OrderBy(s => s.Name.Length)
                .ThenBy(s => SymbolFormat.FqnOf(s), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var output = Paging.Render(
                results, page, max_results, $"declaration(s) matching '{query}'",
                s => $"{SymbolFormat.KindOf(s)} {SymbolFormat.FqnOf(s)} — {SymbolFormat.PrimaryLocation(s, workspace.RelPath)}",
                emptyMessage: $"0 declarations match '{query}'. Try a shorter substring, or search_symbols without the kind filter.");

            return ToolHelpers.WithStaleNotice(output, workspace);
        });

    private static bool MatchesKind(ISymbol symbol, string kindFilter)
    {
        var actual = SymbolFormat.KindOf(symbol);
        if (actual.Contains(kindFilter, StringComparison.OrdinalIgnoreCase))
            return true;
        return kindFilter switch
        {
            "type" => symbol is INamedTypeSymbol,
            "member" => symbol is not INamedTypeSymbol and not INamespaceSymbol,
            _ => false,
        };
    }

    [McpServerTool(Name = "get_symbol")]
    [Description("Everything about one symbol: signature, XML docs, attributes, declaration site(s); for types also base/interfaces and the member list; for members also their overloads. Address by (fuzzy) fully-qualified name — or by file+line to ask 'what is declared here'. Works for NuGet/BCL symbols too (follow up with decompile for their source).")]
    public static Task<string> GetSymbol(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Symbol name, e.g. 'OrderService.Process' or 'System.Text.Json.JsonSerializer'. Omit only when using file+line.")]
        string? symbol = null,
        [Description("Alternative addressing: file path (with line) of a declaration.")]
        string? file = null,
        [Description("1-based line number in 'file'.")]
        int? line = null,
        [Description("Include the full member list for types (default true).")]
        bool include_members = true,
        [Description("Include full docs: parameters, returns, remarks, exceptions (default: summary only).")]
        bool full_docs = false)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "get_symbol", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);

            ISymbol target;
            if (symbol is { Length: > 0 })
            {
                var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
                target = resolved.Symbol;
            }
            else if (file is { Length: > 0 } && line is { } lineNumber)
            {
                target = await SymbolAtLineAsync(solution, workspace, file, lineNumber, ct);
            }
            else
            {
                throw new ToolException("Pass either symbol, or file + line.");
            }

            var output = await DescribeSymbolAsync(target, solution, workspace, include_members, full_docs, ct);
            return ToolHelpers.WithStaleNotice(output, workspace);
        });

    private static async Task<ISymbol> SymbolAtLineAsync(
        Solution solution, RoslynWorkspaceService workspace, string file, int line, CancellationToken ct)
    {
        var document = ToolHelpers.FindDocument(solution, workspace, file);
        var text = await document.GetTextAsync(ct);
        if (line < 1 || line > text.Lines.Count)
            throw new ToolException($"Line {line} is out of range (file has {text.Lines.Count} lines).");

        var span = text.Lines[line - 1].Span;
        var semanticModel = await document.GetSemanticModelAsync(ct)
            ?? throw new ToolException("No semantic model available for this file.");
        var root = await document.GetSyntaxRootAsync(ct)
            ?? throw new ToolException("No syntax tree available for this file.");

        // Prefer a symbol declared on this line; fall back to the first referenced symbol.
        foreach (var node in root.DescendantNodesAndSelf(n => n.Span.IntersectsWith(span)))
        {
            if (!node.Span.IntersectsWith(span))
                continue;
            if (semanticModel.GetDeclaredSymbol(node, ct) is { } declared
                && declared.Kind is not SymbolKind.Local and not SymbolKind.Parameter and not SymbolKind.TypeParameter
                && declared.Locations.Any(l => l.IsInSource && l.GetLineSpan().StartLinePosition.Line == line - 1))
            {
                return declared;
            }
        }

        for (var position = span.Start; position < span.End; position++)
        {
            var referenced = await SymbolFinder.FindSymbolAtPositionAsync(document, position, ct);
            if (referenced is not null)
                return referenced;
        }

        throw new ToolException($"No symbol found at {file}:{line}. Pass the line of a declaration or usage.");
    }

    private static async Task<string> DescribeSymbolAsync(
        ISymbol symbol, Solution solution, RoslynWorkspaceService workspace,
        bool includeMembers, bool fullDocs, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{SymbolFormat.KindOf(symbol)} {SymbolFormat.SignatureOf(symbol)}");
        sb.AppendLine($"fqn: {SymbolFormat.FqnOf(symbol)}");
        if (symbol.GetDocumentationCommentId() is { } docId)
            sb.AppendLine($"id: {docId}");

        var project = ToolHelpers.ProjectOf(solution, symbol);
        sb.AppendLine(project is not null
            ? $"project: {project.Name}"
            : $"assembly: {symbol.ContainingAssembly?.Name ?? "?"} (metadata — use decompile for source)");

        sb.AppendLine("declared: " + string.Join(", ", SymbolFormat.DeclarationLocations(symbol, workspace.RelPath)));

        if (SymbolFormat.DocSummary(symbol, fullDocs) is { } docs)
            sb.AppendLine($"docs: {docs}");

        var attributes = symbol.GetAttributes();
        if (attributes.Length > 0)
        {
            sb.AppendLine("attributes: " + string.Join(", ",
                attributes.Take(12).Select(a => "[" + (a.AttributeClass?.Name.Replace("Attribute", "") ?? "?") + "]")));
        }

        if (symbol is INamedTypeSymbol type)
        {
            DescribeType(sb, type, includeMembers, workspace);
        }
        else if (symbol.ContainingType is { } containingType && symbol is IMethodSymbol or IPropertySymbol)
        {
            var overloads = containingType.GetMembers(symbol.Name)
                .Where(m => !SymbolEqualityComparer.Default.Equals(m, symbol))
                .Take(8)
                .ToList();
            if (overloads.Count > 0)
            {
                sb.AppendLine($"overloads ({overloads.Count}):");
                foreach (var overload in overloads)
                    sb.AppendLine($"  {SymbolFormat.SignatureOf(overload)}");
            }
        }

        if (symbol is IMethodSymbol { IsExtensionMethod: true } ext && ext.ReceiverType is not null)
            sb.AppendLine($"extends: {ext.ReceiverType.ToDisplayString(SymbolFormat.Fqn)}");

        return sb.ToString().TrimEnd();
    }

    private static void DescribeType(StringBuilder sb, INamedTypeSymbol type, bool includeMembers, RoslynWorkspaceService workspace)
    {
        if (type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
        {
            var chain = new List<string>();
            for (var current = baseType; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
                chain.Add(current.ToDisplayString(SymbolFormat.Fqn));
            if (chain.Count > 0)
                sb.AppendLine("base: " + string.Join(" ← ", chain));
        }

        if (type.AllInterfaces.Length > 0)
        {
            sb.AppendLine("implements: " + string.Join(", ",
                type.AllInterfaces.Take(15).Select(i => i.ToDisplayString(SymbolFormat.Fqn))));
        }

        if (!includeMembers)
            return;

        var members = type.GetMembers()
            .Where(m => !m.IsImplicitlyDeclared)
            .Where(m => m is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
            .OrderByDescending(m => m.DeclaredAccessibility == Accessibility.Public)
            .ThenBy(m => m.Kind switch { SymbolKind.Field => 0, SymbolKind.Property => 1, SymbolKind.Event => 2, _ => 3 })
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        const int cap = 60;
        sb.AppendLine($"members ({members.Count}{(members.Count > cap ? $", showing {cap} — use search_symbols to find the rest" : "")}):");
        foreach (var member in members.Take(cap))
        {
            var line = member.Locations.FirstOrDefault(l => l.IsInSource) is { } loc
                ? "  — " + SymbolFormat.Location(loc, workspace.RelPath)
                : "";
            sb.AppendLine($"  {SymbolFormat.SignatureOf(member)}{line}");
        }
    }

    [McpServerTool(Name = "get_file_outline")]
    [Description("Token-compact structure of one source file: every namespace/type/member with signature and line range, in ~1% of the tokens of reading the file. Use before reading a large file to know which lines to read.")]
    public static Task<string> GetFileOutline(
        RoslynWorkspaceService workspace,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("File path: absolute, solution-relative, or unique suffix.")]
        string file)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "get_file_outline", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var document = ToolHelpers.FindDocument(solution, workspace, file);
            var root = await document.GetSyntaxRootAsync(ct)
                ?? throw new ToolException("File has no syntax tree.");
            var semanticModel = await document.GetSemanticModelAsync(ct)
                ?? throw new ToolException("File has no semantic model.");
            var text = await document.GetTextAsync(ct);

            var entries = new List<(ISymbol Symbol, int StartLine, int EndLine)>();
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

            foreach (var node in root.DescendantNodesAndSelf())
            {
                ct.ThrowIfCancellationRequested();
                var declared = semanticModel.GetDeclaredSymbol(node, ct);
                if (declared is null || !seen.Add(declared))
                    continue;
                if (declared.Kind is SymbolKind.Local or SymbolKind.Parameter or SymbolKind.TypeParameter
                    or SymbolKind.RangeVariable or SymbolKind.Label or SymbolKind.Alias)
                    continue;
                if (declared is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove })
                    continue;
                if (declared is INamespaceSymbol)
                    continue;

                var lineSpan = node.GetLocation().GetLineSpan();
                entries.Add((declared, lineSpan.StartLinePosition.Line + 1, lineSpan.EndLinePosition.Line + 1));
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{workspace.RelPath(document.FilePath)} — {text.Lines.Count} lines, {document.Project.Language}"
                          + $" (project {document.Project.Name})");

            foreach (var (declared, startLine, endLine) in entries.OrderBy(e => e.StartLine))
            {
                var depth = ContainmentDepth(declared);
                var range = startLine == endLine ? $"[{startLine}]" : $"[{startLine}-{endLine}]";
                sb.AppendLine($"{new string(' ', depth * 2)}{SymbolFormat.KindOf(declared)} {SymbolFormat.SignatureOf(declared)}  {range}");
            }

            if (entries.Count == 0)
                sb.AppendLine("(no declarations — file may be empty or non-code)");

            return ToolHelpers.WithStaleNotice(sb.ToString().TrimEnd(), workspace);
        });

    private static int ContainmentDepth(ISymbol symbol)
    {
        var depth = 0;
        for (var current = symbol.ContainingType; current is not null; current = current.ContainingType)
            depth++;
        return depth;
    }
}
