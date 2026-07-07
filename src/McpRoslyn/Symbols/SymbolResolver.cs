using McpRoslyn.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace McpRoslyn.Symbols;

/// <summary>A resolved symbol plus the project whose compilation produced it.</summary>
public sealed record ResolvedSymbol(ISymbol Symbol, Project Project);

/// <summary>
/// Resolves agent-supplied symbol names: fuzzy fully-qualified names ("OrderService.Process"),
/// optional parameter lists ("Process(int, string)"), doc-comment IDs ("M:Ns.T.M(System.Int32)"),
/// and metadata-only symbols from referenced assemblies. Ambiguity throws a teaching error whose
/// candidate list is directly pastable back into any tool.
/// </summary>
public static class SymbolResolver
{
    public static async Task<ResolvedSymbol> ResolveOrThrowAsync(
        Solution solution, string query, CancellationToken ct, string? projectHint = null)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ToolException("Empty symbol name. Pass a (fuzzy) fully-qualified name like 'MyApp.OrderService.Process'.");

        query = query.Trim();
        // An inline 'Name@ProjectHint' suffix disambiguates same-FQN declarations (linked file /
        // two projects) and overrides an explicit hint argument.
        (query, var inlineHint) = SplitProjectHint(query);
        projectHint = inlineHint ?? projectHint;

        // Doc-comment ID fast path ("T:", "M:", "P:", "F:", "E:", "N:").
        if (query.Length > 2 && query[1] == ':' && "TMPFEN".Contains(query[0]))
        {
            var byId = NarrowByProject(DedupeByIdentity(await ResolveDocIdAsync(solution, query, ct).ConfigureAwait(false)), projectHint);
            return byId.Count switch
            {
                1 => byId[0],
                0 => throw new ToolException(
                    $"Doc-comment ID '{query}' did not resolve in the loaded solution. Try search_symbols with the plain name."),
                _ => throw AmbiguousMatchError(query, byId),
            };
        }

        var parsed = ParseQuery(query);
        var matches = await FindSourceMatchesAsync(solution, parsed, ct).ConfigureAwait(false);

        if (matches.Count == 0)
            matches = await FindMetadataMatchesAsync(solution, parsed, ct).ConfigureAwait(false);

        // Prefer exact-name matches over case-insensitive ones when both exist.
        if (matches.Count > 1)
        {
            var exact = matches
                .Where(m => LastNamePart(m.Symbol) == parsed.Segments[^1])
                .ToList();
            if (exact.Count >= 1)
                matches = exact;
        }

        matches = NarrowByProject(DedupeByIdentity(matches), projectHint);

        switch (matches.Count)
        {
            case 1:
                return matches[0];
            case 0:
                var suggestions = await SuggestAsync(solution, parsed.Segments[^1], ct).ConfigureAwait(false);
                throw new ToolException(
                    $"Symbol '{query}' not found in the loaded solution or its references."
                    + (suggestions.Count > 0
                        ? " Similar declarations:\n  " + string.Join("\n  ", suggestions)
                        : " Use search_symbols to explore."));
            default:
                throw AmbiguousMatchError(query, matches);
        }
    }

    /// <summary>
    /// Distinct declarations that share a fully-qualified name (the same type defined in two
    /// projects, or a linked file compiled into two assemblies) also share a doc-id, so the error
    /// prints the project and location too — that is what actually tells them apart.
    /// </summary>
    private static ToolException AmbiguousMatchError(string query, List<ResolvedSymbol> matches)
    {
        var lines = matches.Take(10).Select(m =>
        {
            var id = m.Symbol.GetDocumentationCommentId() ?? SymbolFormat.FqnOf(m.Symbol);
            var where = SymbolFormat.PrimaryLocation(m.Symbol, p => p is null ? "?" : Path.GetFileName(p));
            return $"{id}  ({SymbolFormat.KindOf(m.Symbol)}) — project {m.Project.Name} @ {where}";
        });
        return new ToolException(
            $"'{query}' is ambiguous — {matches.Count} matches. Re-query with a project discriminator "
            + "(append '@<projectName>', or pass project= where the tool accepts it), or address one "
            + "declaration by file+line (get_symbol):\n  " + string.Join("\n  ", lines));
    }

    /// <summary>When ambiguous and a hint is given, keep only matches in a project (or assembly)
    /// whose name contains the hint; if that eliminates everything, keep all so the ambiguity is
    /// still reported rather than turned into a spurious not-found.</summary>
    private static List<ResolvedSymbol> NarrowByProject(List<ResolvedSymbol> matches, string? projectHint)
    {
        if (matches.Count <= 1 || string.IsNullOrWhiteSpace(projectHint))
            return matches;
        var narrowed = matches
            .Where(m => m.Project.Name.Contains(projectHint, StringComparison.OrdinalIgnoreCase)
                        || m.Symbol.ContainingAssembly?.Name.Contains(projectHint, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
        return narrowed.Count > 0 ? narrowed : matches;
    }

    /// <summary>Splits a trailing 'Name@ProjectHint' discriminator off a query (top level only, and
    /// not a leading verbatim-identifier '@'). Returns the bare query and the hint (or null).</summary>
    private static (string Query, string? Hint) SplitProjectHint(string query)
    {
        var depth = 0;
        for (var i = query.Length - 1; i > 0; i--)
        {
            var c = query[i];
            if (c is ')' or '>' or ']')
                depth++;
            else if (c is '(' or '<' or '[')
                depth--;
            else if (c == '@' && depth == 0)
            {
                var prev = query[i - 1];
                if (char.IsLetterOrDigit(prev) || prev is '_' or ')' or '>' or ']')
                {
                    var hint = query[(i + 1)..].Trim();
                    if (hint.Length > 0 && !hint.Contains('(') && !hint.Contains('<'))
                        return (query[..i].Trim(), hint);
                }
                return (query, null);
            }
        }
        return (query, null);
    }

    /// <summary>All source declarations matching a query (every project flavor, undeduped) — used by
    /// scans that must enumerate members from each flavor rather than resolve to a single symbol.</summary>
    public static Task<List<ResolvedSymbol>> FindAllSourceMatchesAsync(Solution solution, string query, CancellationToken ct)
    {
        (query, _) = SplitProjectHint(query.Trim());
        return FindSourceMatchesAsync(solution, ParseQuery(query), ct);
    }

    // ---------------------------------------------------------------- query parsing

    public sealed record ParsedQuery(string[] Segments, string[]? ParameterTypes, int? Arity, bool IsConstructor);

    public static ParsedQuery ParseQuery(string query)
    {
        string[]? parameters = null;
        var parenIndex = query.IndexOf('(');
        if (parenIndex >= 0)
        {
            var closing = query.LastIndexOf(')');
            var inner = closing > parenIndex ? query[(parenIndex + 1)..closing] : query[(parenIndex + 1)..];
            parameters = SplitTopLevel(inner)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .Select(StripParameterName)
                .ToArray();
            query = query[..parenIndex];
        }

        var rawSegments = query.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int? arity = null;
        var isCtor = false;
        var segments = new List<string>(rawSegments.Length);
        foreach (var raw in rawSegments)
        {
            var segment = raw.Replace("+", ".");
            var lt = segment.IndexOf('<');
            if (lt >= 0)
            {
                var gt = segment.LastIndexOf('>');
                var inner = gt > lt ? segment[(lt + 1)..gt] : "";
                arity = inner.Length == 0 ? 1 : inner.Count(c => c == ',') + 1;
                segment = segment[..lt];
            }
            var backtick = segment.IndexOf('`');
            if (backtick >= 0)
            {
                if (int.TryParse(segment[(backtick + 1)..], out var a))
                    arity = a;
                segment = segment[..backtick];
            }
            // "Type.ctor" / "Type.#ctor" addresses constructors.
            if (segment is "ctor" or "#ctor" or ".ctor")
            {
                isCtor = true;
                continue;
            }
            if (segment.Contains('.'))
                segments.AddRange(segment.Split('.', StringSplitOptions.RemoveEmptyEntries));
            else
                segments.Add(segment);
        }

        if (segments.Count == 0)
            throw new ToolException($"Could not parse symbol name '{query}'.");

        return new ParsedQuery([.. segments], parameters, arity, isCtor);
    }

    private static readonly string[] ParameterModifiers = ["out ", "ref readonly ", "ref ", "in ", "params "];

    private static string StripParameterName(string parameter)
    {
        // "int count" -> "int"; "out string x" -> "string"; keep generic args intact.
        // Strip only a LEADING modifier keyword — a global Replace would corrupt type names that
        // merely contain "in"/"out"/"ref" before the parameter-name space (e.g. "Plugin plugin").
        var cleaned = parameter.Trim();
        foreach (var modifier in ParameterModifiers)
        {
            if (cleaned.StartsWith(modifier, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[modifier.Length..].TrimStart();
                break;
            }
        }
        var depth = 0;
        var lastSpace = -1;
        for (var i = 0; i < cleaned.Length; i++)
        {
            switch (cleaned[i])
            {
                case '<' or '[' or '(':
                    depth++;
                    break;
                case '>' or ']' or ')':
                    depth--;
                    break;
                case ' ' when depth == 0:
                    lastSpace = i;
                    break;
            }
        }
        return lastSpace > 0 ? cleaned[..lastSpace].Trim() : cleaned;
    }

    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<' or '[' or '(':
                    depth++;
                    break;
                case '>' or ']' or ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return text[start..i];
                    start = i + 1;
                    break;
            }
        }
        if (start <= text.Length - 1 || text.Length == 0)
            yield return text[start..];
    }

    // ---------------------------------------------------------------- source resolution

    private static async Task<List<ResolvedSymbol>> FindSourceMatchesAsync(
        Solution solution, ParsedQuery parsed, CancellationToken ct)
    {
        var declarations = await SymbolFinder.FindSourceDeclarationsAsync(solution, parsed.Segments[^1], ignoreCase: true, ct)
            .ConfigureAwait(false);

        var results = new List<ResolvedSymbol>();
        foreach (var symbol in declarations)
        {
            ct.ThrowIfCancellationRequested();
            if (!MatchesSegments(symbol, parsed.Segments))
                continue;
            if (parsed.Arity is { } arity && symbol is INamedTypeSymbol nt && nt.Arity != arity)
                continue;

            if (parsed.IsConstructor)
            {
                if (symbol is not INamedTypeSymbol type)
                    continue;
                foreach (var ctor in type.InstanceConstructors.Where(c => MatchesParameters(c, parsed.ParameterTypes)))
                {
                    var ctorProject = ProjectOf(solution, ctor) ?? ProjectOf(solution, type);
                    if (ctorProject is not null)
                        results.Add(new ResolvedSymbol(ctor, ctorProject));
                }
                continue;
            }

            if (!MatchesParameters(symbol, parsed.ParameterTypes))
                continue;

            var project = ProjectOf(solution, symbol);
            if (project is not null)
                results.Add(new ResolvedSymbol(symbol, project));
        }

        return results;
    }

    private static Project? ProjectOf(Solution solution, ISymbol symbol)
    {
        foreach (var location in symbol.Locations)
        {
            if (location.SourceTree is { } tree && solution.GetDocument(tree) is { } document)
                return document.Project;
        }
        return null;
    }

    private static string LastNamePart(ISymbol symbol) =>
        symbol.Name.Length > 0 ? symbol.Name : symbol.MetadataName;

    /// <summary>The symbol's name chain must end with the query segments (case-insensitive, on dot boundaries).</summary>
    public static bool MatchesSegments(ISymbol symbol, string[] segments)
    {
        var current = symbol;
        // Match the last segment against the symbol name itself.
        if (!NameMatches(current, segments[^1]))
            return false;

        var index = segments.Length - 2;
        current = Containing(current);
        while (index >= 0)
        {
            if (current is null or INamespaceSymbol { IsGlobalNamespace: true })
                return false;
            if (!NameMatches(current, segments[index]))
                return false;
            index--;
            current = Containing(current);
        }
        return true;
    }

    private static ISymbol? Containing(ISymbol symbol) =>
        (ISymbol?)symbol.ContainingType ?? symbol.ContainingNamespace;

    private static bool NameMatches(ISymbol symbol, string segment) =>
        string.Equals(symbol.Name, segment, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesParameters(ISymbol symbol, string[]? parameterTypes)
    {
        if (parameterTypes is null)
            return true;

        var parameters = symbol switch
        {
            IMethodSymbol m => m.Parameters,
            IPropertySymbol p => p.Parameters,
            _ => default,
        };

        if (parameters.IsDefault)
            return parameterTypes.Length == 0;
        if (parameters.Length != parameterTypes.Length)
            return false;

        for (var i = 0; i < parameters.Length; i++)
        {
            var expected = NormalizeTypeName(parameterTypes[i]);
            var actualShort = NormalizeTypeName(parameters[i].Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            var actualFull = NormalizeTypeName(parameters[i].Type.ToDisplayString(SymbolFormat.Fqn));
            if (!actualShort.Equals(expected, StringComparison.OrdinalIgnoreCase)
                && !actualFull.Equals(expected, StringComparison.OrdinalIgnoreCase)
                && !actualFull.EndsWith("." + expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    // The parameter is rendered with UseSpecialTypes (keywords), so a user-supplied fully-qualified
    // BCL name must be mapped to the keyword to match. Covers every C# primitive alias, not just int.
    private static readonly (string Fqn, string Keyword)[] PrimitiveAliases =
    [
        ("System.Int32", "int"), ("System.Int64", "long"), ("System.Int16", "short"),
        ("System.UInt32", "uint"), ("System.UInt64", "ulong"), ("System.UInt16", "ushort"),
        ("System.Byte", "byte"), ("System.SByte", "sbyte"),
        ("System.Double", "double"), ("System.Single", "float"), ("System.Decimal", "decimal"),
        ("System.Boolean", "bool"), ("System.Char", "char"), ("System.String", "string"),
        ("System.Object", "object"), ("System.IntPtr", "nint"), ("System.UIntPtr", "nuint"),
        ("System.Void", "void"),
    ];

    private static string NormalizeTypeName(string name)
    {
        var normalized = name.Replace(" ", "");
        foreach (var (fqn, keyword) in PrimitiveAliases)
            normalized = normalized.Replace(fqn, keyword);
        return normalized;
    }

    // ---------------------------------------------------------------- metadata resolution

    private static async Task<List<ResolvedSymbol>> FindMetadataMatchesAsync(
        Solution solution, ParsedQuery parsed, CancellationToken ct)
    {
        // Metadata names are fully qualified, so any project that can see the assembly resolves
        // the same symbol (deduped by assembly+doc-ID anyway). Probe cheaply first: projects whose
        // compilation is already realized. Only then force compilations, stopping at the first
        // project that yields a match — never realize the whole solution for one lookup. All TFM
        // flavors are probed, so a package referenced only by a non-first framework still resolves.
        var projects = solution.Projects.ToList();
        var realized = new List<(Project Project, Compilation Compilation)>();
        var unrealized = new List<Project>();
        foreach (var project in projects)
        {
            if (project.TryGetCompilation(out var compilation))
                realized.Add((project, compilation));
            else
                unrealized.Add(project);
        }

        var results = new List<ResolvedSymbol>();
        var seenAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (project, compilation) in realized)
        {
            ct.ThrowIfCancellationRequested();
            ProbeCompilation(compilation, project, parsed, results, seenAssemblies);
        }
        if (results.Count > 0)
            return results;

        // Bigger reference closures first: they can see the most assemblies.
        foreach (var project in unrealized.OrderByDescending(p => p.MetadataReferences.Count))
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            if (compilation is null)
                continue;
            ProbeCompilation(compilation, project, parsed, results, seenAssemblies);
            if (results.Count > 0)
                return results;
        }

        return results;
    }

    private static void ProbeCompilation(
        Compilation compilation, Project project, ParsedQuery parsed,
        List<ResolvedSymbol> results, HashSet<string> seenAssemblies)
    {
        // Try the full segment list as a type, then all-but-last as a type + member.
        var asType = FindMetadataType(compilation, parsed.Segments, parsed.Arity);
        if (asType is not null && !parsed.IsConstructor && parsed.ParameterTypes is null)
        {
            if (seenAssemblies.Add($"{AssemblyIdentityOf(asType)}|{asType.GetDocumentationCommentId()}"))
                results.Add(new ResolvedSymbol(asType, project));
            return;
        }

        var parentSegments = parsed.IsConstructor ? parsed.Segments : parsed.Segments[..^1];
        if (parentSegments.Length == 0)
            return;
        var parentType = FindMetadataType(compilation, parentSegments, parsed.IsConstructor ? parsed.Arity : null);
        if (parentType is null)
            return;

        var members = parsed.IsConstructor
            ? parentType.InstanceConstructors.Cast<ISymbol>()
            : parentType.GetMembers().Where(m => string.Equals(m.Name, parsed.Segments[^1], StringComparison.OrdinalIgnoreCase));

        foreach (var member in members.Where(m => MatchesParameters(m, parsed.ParameterTypes)))
        {
            if (seenAssemblies.Add($"{AssemblyIdentityOf(member)}|{member.GetDocumentationCommentId()}"))
                results.Add(new ResolvedSymbol(member, project));
        }
    }

    // Full assembly identity (name + version + culture + key), so two projects referencing different
    // versions of the same package are not deduped into one — they are genuinely different symbols.
    private static string AssemblyIdentityOf(ISymbol symbol) =>
        symbol.ContainingAssembly?.Identity.GetDisplayName() ?? "?";

    /// <summary>Projects deduplicated by project file (multi-TFM projects appear once per TFM).</summary>
    public static IEnumerable<Project> DistinctByProjectFile(Solution solution) =>
        solution.Projects
            .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

    private static INamedTypeSymbol? FindMetadataType(Compilation compilation, string[] segments, int? arity)
    {
        // Try progressively shorter namespace+type prefixes, descending the rest as nested types.
        for (var split = segments.Length; split >= 1; split--)
        {
            var baseName = string.Join(".", segments[..split]);
            foreach (var candidateArity in ArityCandidates(arity, isLast: split == segments.Length))
            {
                var metadataName = candidateArity == 0 ? baseName : $"{baseName}`{candidateArity}";
                var type = compilation.GetTypeByMetadataName(metadataName);
                if (type is null)
                    continue;

                var current = type;
                var ok = true;
                for (var i = split; i < segments.Length && ok; i++)
                {
                    var nested = current.GetTypeMembers()
                        .FirstOrDefault(t => string.Equals(t.Name, segments[i], StringComparison.OrdinalIgnoreCase)
                                             && (arity is null || i < segments.Length - 1 || t.Arity == arity));
                    if (nested is null)
                        ok = false;
                    else
                        current = nested;
                }
                if (ok)
                    return current;
            }
        }
        return null;
    }

    private static IEnumerable<int> ArityCandidates(int? arity, bool isLast)
    {
        if (arity is { } a && isLast)
        {
            yield return a;
            yield break;
        }
        for (var i = 0; i <= 4; i++)
            yield return i;
    }

    // ---------------------------------------------------------------- misc

    private static List<ResolvedSymbol> DedupeByIdentity(List<ResolvedSymbol> matches) =>
        matches
            .GroupBy(m => SymbolFormat.IdentityKey(m.Symbol))
            .Select(g => g.OrderByDescending(m => m.Symbol.Locations.Any(l => l.IsInSource)).First())
            .ToList();

    private static async Task<List<string>> SuggestAsync(Solution solution, string name, CancellationToken ct)
    {
        try
        {
            var found = await SymbolFinder.FindSourceDeclarationsWithPatternAsync(
                solution, name, SymbolFilter.TypeAndMember, ct).ConfigureAwait(false);
            return found
                .Take(8)
                .Select(s => $"{SymbolFormat.FqnOf(s)}  ({SymbolFormat.KindOf(s)})")
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<ResolvedSymbol>> ResolveDocIdAsync(Solution solution, string docId, CancellationToken ct)
    {
        // Doc-IDs are this server's own re-query currency (ambiguity errors print them). Probe every
        // realized compilation first (cheap; a warm workspace needs no forcing), collecting each
        // distinct match — a source doc-id can legitimately answer in several projects (a shared
        // file compiled into two assemblies, or two projects sharing an FQN), and those must surface
        // as ambiguity rather than an arbitrary silent pick.
        var results = new List<ResolvedSymbol>();
        var seen = new HashSet<string>();
        var foundSource = false;

        void Probe(Compilation compilation, Project project)
        {
            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(docId, compilation);
            if (symbol is null)
                return;
            if (seen.Add(SymbolFormat.IdentityKey(symbol)))
                results.Add(new ResolvedSymbol(symbol, project));
            if (symbol.Locations.Any(l => l.IsInSource))
                foundSource = true;
        }

        var unrealized = new List<Project>();
        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project.TryGetCompilation(out var compilation))
                Probe(compilation, project);
            else
                unrealized.Add(project);
        }

        // A metadata-only answer is identical wherever it resolves (same assembly identity dedupes),
        // so a realized metadata hit needs no forcing. Otherwise force the rest — to find the symbol
        // at all, or to catch a same-FQN source duplicate hiding in an unrealized project.
        if (results.Count == 0 || foundSource)
        {
            foreach (var project in unrealized.OrderByDescending(p => p.MetadataReferences.Count))
            {
                ct.ThrowIfCancellationRequested();
                var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                if (compilation is not null)
                    Probe(compilation, project);
            }
        }

        return results;
    }
}
