using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace McpRoslyn.Symbols;

public static class SymbolFormat
{
    /// <summary>Fully qualified (no global::), e.g. MyApp.Orders.OrderService.Process(int, string).</summary>
    public static readonly SymbolDisplayFormat Fqn = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Declaration-style signature: accessibility, modifiers, type, name, parameters.</summary>
    public static readonly SymbolDisplayFormat Signature = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
        memberOptions: SymbolDisplayMemberOptions.IncludeAccessibility
                       | SymbolDisplayMemberOptions.IncludeModifiers
                       | SymbolDisplayMemberOptions.IncludeType
                       | SymbolDisplayMemberOptions.IncludeParameters
                       | SymbolDisplayMemberOptions.IncludeExplicitInterface
                       | SymbolDisplayMemberOptions.IncludeConstantValue,
        parameterOptions: SymbolDisplayParameterOptions.IncludeType
                          | SymbolDisplayParameterOptions.IncludeName
                          | SymbolDisplayParameterOptions.IncludeParamsRefOut
                          | SymbolDisplayParameterOptions.IncludeDefaultValue
                          | SymbolDisplayParameterOptions.IncludeExtensionThis,
        propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
        delegateStyle: SymbolDisplayDelegateStyle.NameAndSignature,
        kindOptions: SymbolDisplayKindOptions.IncludeMemberKeyword
                     | SymbolDisplayKindOptions.IncludeTypeKeyword
                     | SymbolDisplayKindOptions.IncludeNamespaceKeyword,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    public static string FqnOf(ISymbol symbol) => symbol.ToDisplayString(Fqn);

    public static string SignatureOf(ISymbol symbol) => symbol.ToDisplayString(Signature);

    /// <summary>
    /// Stable identity key that collapses genuine duplicates while keeping distinct declarations
    /// apart. Source symbols key on their declaration file+span set, so a symbol seen once per
    /// multi-target TFM flavor (same file, same span) collapses to one, but two different projects
    /// declaring the same fully-qualified name stay separate. Metadata symbols key on the
    /// containing assembly's identity plus documentation id, so the same referenced assembly
    /// reached through several projects collapses while different assembly versions do not.
    /// </summary>
    public static string IdentityKey(ISymbol symbol)
    {
        var sourceSpans = symbol.Locations
            .Where(l => l.IsInSource && l.SourceTree is not null)
            .Select(l => $"{l.SourceTree!.FilePath}:{l.SourceSpan.Start}:{l.SourceSpan.Length}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (sourceSpans.Count > 0)
            return "S|" + string.Join(";", sourceSpans);

        var assembly = symbol.ContainingAssembly?.Identity.GetDisplayName() ?? "?";
        return "M|" + assembly + "|" + (symbol.GetDocumentationCommentId() ?? FqnOf(symbol) + symbol.Kind);
    }

    public static string KindOf(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol nt => nt.TypeKind switch
        {
            TypeKind.Class => nt.IsRecord ? "record" : "class",
            TypeKind.Struct => nt.IsRecord ? "record-struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            _ => nt.TypeKind.ToString().ToLowerInvariant(),
        },
        IMethodSymbol m => m.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => "ctor",
            MethodKind.LocalFunction => "local-function",
            MethodKind.UserDefinedOperator or MethodKind.BuiltinOperator or MethodKind.Conversion => "operator",
            _ => m.IsExtensionMethod ? "extension-method" : "method",
        },
        IPropertySymbol p => p.IsIndexer ? "indexer" : "property",
        IFieldSymbol f => f.ContainingType?.TypeKind == TypeKind.Enum ? "enum-member" : f.IsConst ? "const" : "field",
        IEventSymbol => "event",
        INamespaceSymbol => "namespace",
        IParameterSymbol => "parameter",
        ILocalSymbol => "local",
        ITypeParameterSymbol => "type-parameter",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    /// <summary>"path/rel.cs:12" or "[AssemblyName] (metadata)" for a symbol's primary location.</summary>
    public static string PrimaryLocation(ISymbol symbol, Func<string?, string> relPath)
    {
        var source = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (source is not null)
            return Location(source, relPath);
        return $"[{symbol.ContainingAssembly?.Name ?? "?"}] (metadata — use decompile to view)";
    }

    public static string Location(Location location, Func<string?, string> relPath)
    {
        if (!location.IsInSource)
            return "(metadata)";
        var span = location.GetLineSpan();
        return $"{relPath(span.Path)}:{span.StartLinePosition.Line + 1}";
    }

    /// <summary>All source declaration locations (partial types can have several).</summary>
    public static IEnumerable<string> DeclarationLocations(ISymbol symbol, Func<string?, string> relPath)
    {
        var any = false;
        foreach (var location in symbol.Locations.Where(l => l.IsInSource))
        {
            any = true;
            yield return Location(location, relPath);
        }
        if (!any)
            yield return $"[{symbol.ContainingAssembly?.Name ?? "?"}] (metadata — use decompile to view)";
    }

    /// <summary>Plain-text summary from the XML doc comment, or null.</summary>
    public static string? DocSummary(ISymbol symbol, bool full = false, int maxLength = 600)
    {
        string? xml = null;
        try
        {
            xml = symbol.GetDocumentationCommentXml(expandIncludes: true);
        }
        catch
        {
            // Doc providers can throw on odd assemblies; docs are best-effort.
        }

        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var doc = XElement.Parse($"<root>{xml}</root>");
            var inner = doc.Elements().Count() == 1 && doc.Elements().First().Name.LocalName is "member" or "doc"
                ? doc.Elements().First()
                : doc;

            var sb = new StringBuilder();
            AppendSection(sb, inner, "summary", null);
            if (full)
            {
                foreach (var param in inner.Descendants("param"))
                    AppendText(sb, $"param {param.Attribute("name")?.Value}: ", param);
                AppendSection(sb, inner, "returns", "returns: ");
                AppendSection(sb, inner, "remarks", "remarks: ");
                foreach (var ex in inner.Descendants("exception"))
                    AppendText(sb, $"throws {ShortCref(ex.Attribute("cref")?.Value)}: ", ex);
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0)
                return null;
            if (!full && text.Length > maxLength)
                text = text[..maxLength] + "…";
            return text;
        }
        catch
        {
            return null;
        }
    }

    private static void AppendSection(StringBuilder sb, XElement root, string element, string? prefix)
    {
        var el = root.Descendants(element).FirstOrDefault();
        if (el is not null)
            AppendText(sb, prefix ?? "", el);
    }

    private static void AppendText(StringBuilder sb, string prefix, XElement element)
    {
        var text = RenderDocElement(element);
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (sb.Length > 0)
            sb.AppendLine();
        sb.Append(prefix).Append(text);
    }

    private static string RenderDocElement(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText t:
                    sb.Append(t.Value);
                    break;
                case XElement el when el.Name.LocalName is "see" or "seealso":
                    sb.Append(ShortCref(el.Attribute("cref")?.Value ?? el.Attribute("langword")?.Value ?? el.Value));
                    break;
                case XElement el when el.Name.LocalName is "paramref" or "typeparamref":
                    sb.Append(el.Attribute("name")?.Value);
                    break;
                case XElement el:
                    sb.Append(RenderDocElement(el));
                    break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    private static string ShortCref(string? cref)
    {
        if (string.IsNullOrEmpty(cref))
            return "";
        var name = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = name.IndexOf('(');
        if (paren >= 0)
            name = name[..paren];
        var lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot < name.Length - 1 ? name[(lastDot + 1)..] : name;
    }

    /// <summary>The trimmed source line at a span (for context snippets), capped in length.</summary>
    public static string LineSnippet(SourceText text, TextSpan span, int maxLength = 160)
    {
        var line = text.Lines.GetLineFromPosition(Math.Min(span.Start, text.Length));
        var snippet = line.ToString().Trim();
        return snippet.Length > maxLength ? snippet[..maxLength] + "…" : snippet;
    }
}
