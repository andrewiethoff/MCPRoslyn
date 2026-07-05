using System.Collections.Concurrent;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using McpRoslyn.Infrastructure;
using McpRoslyn.Symbols;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ISymbol = Microsoft.CodeAnalysis.ISymbol;

namespace McpRoslyn.Decompilation;

/// <summary>
/// Metadata-as-source: decompiles NuGet/BCL symbols with ICSharpCode.Decompiler. Reference
/// assemblies (empty bodies) are transparently swapped for the runtime implementation assembly
/// when one can be located. CSharpDecompiler is not thread-safe, so cached instances are used
/// single-flight per assembly; the cache is freshness-checked (rebuilt DLLs are re-read), bounded,
/// and cleared on solution (re)load.
/// </summary>
public sealed class DecompilerService(ILogger<DecompilerService> log)
{
    private const int MaxOutputChars = 40_000;
    private const int MaxCachedDecompilers = 16;

    private sealed record CacheEntry(CSharpDecompiler Decompiler, DateTime LastWriteUtc, long Length)
    {
        public object Gate { get; } = new();
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _decompilers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops all cached decompilers (called when a solution is (re)loaded).</summary>
    public void Clear() => _decompilers.Clear();

    public async Task<string> DecompileAsync(ResolvedSymbol resolved, CancellationToken ct)
    {
        var symbol = resolved.Symbol;

        var compilation = await resolved.Project.GetCompilationAsync(ct).ConfigureAwait(false)
            ?? throw new ToolException("Project has no compilation — check workspace_status.");

        var assemblySymbol = symbol.ContainingAssembly
            ?? throw new ToolException("Symbol has no containing assembly (namespaces cannot be decompiled).");

        var reference = compilation.GetMetadataReference(assemblySymbol) as PortableExecutableReference;
        var assemblyPath = reference?.FilePath;
        if (assemblyPath is null || !File.Exists(assemblyPath))
            throw new ToolException($"Could not locate the assembly file for [{assemblySymbol.Name}].");

        var isReferenceAssembly = assemblySymbol.GetAttributes()
            .Any(a => a.AttributeClass?.Name == "ReferenceAssemblyAttribute");

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType
            ?? throw new ToolException($"Cannot decompile a {SymbolFormat.KindOf(symbol)} — pass a type or type member.");

        var reflectionName = ReflectionName(type);

        foreach (var candidate in CandidatePaths(assemblyPath, isReferenceAssembly))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var entry = GetEntry(candidate);
                string code;

                // CSharpDecompiler instances are not thread-safe: single-flight per assembly.
                lock (entry.Gate)
                {
                    var decompiler = entry.Decompiler;
                    decompiler.CancellationToken = ct;
                    try
                    {
                        var typeDefinition = decompiler.TypeSystem.FindType(new FullTypeName(reflectionName)).GetDefinition();
                        if (typeDefinition is null
                            || !string.Equals(typeDefinition.ParentModule?.MetadataFile?.FileName, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            continue; // Type not defined in this module (facade/forwarder) — next candidate.
                        }

                        code = symbol is INamedTypeSymbol
                            ? decompiler.DecompileTypeAsString(new FullTypeName(reflectionName))
                            : DecompileMembers(decompiler, typeDefinition, symbol);
                    }
                    finally
                    {
                        decompiler.CancellationToken = CancellationToken.None;
                    }
                }

                if (code.Length > MaxOutputChars)
                    code = code[..MaxOutputChars] + $"\n… truncated at {MaxOutputChars} chars.";

                var header = $"// decompiled from {candidate}\n";
                if (isReferenceAssembly && candidate.Equals(assemblyPath, StringComparison.OrdinalIgnoreCase))
                    header += "// NOTE: reference assembly — signatures are exact but method bodies are stubs.\n";
                return header + code;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ToolException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Decompilation from {Path} failed; trying next candidate", candidate);
            }
        }

        throw new ToolException(
            $"Could not decompile {SymbolFormat.FqnOf(symbol)} from [{assemblySymbol.Name}]. "
            + "The defining module may be unavailable on this machine.");
    }

    private CacheEntry GetEntry(string path)
    {
        var info = new FileInfo(path);
        var lastWrite = info.LastWriteTimeUtc;
        var length = info.Length;

        if (_decompilers.TryGetValue(path, out var existing)
            && existing.LastWriteUtc == lastWrite
            && existing.Length == length)
        {
            return existing;
        }

        // Crude bound: agents revisit few assemblies; a full clear on overflow is fine.
        if (_decompilers.Count >= MaxCachedDecompilers && !_decompilers.ContainsKey(path))
            _decompilers.Clear();

        var fresh = new CacheEntry(CreateDecompiler(path), lastWrite, length);
        _decompilers[path] = fresh;
        return fresh;
    }

    private static CSharpDecompiler CreateDecompiler(string path)
    {
        var settings = new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            ShowXmlDocumentation = true,
        };
        return new CSharpDecompiler(path, settings);
    }

    private static string DecompileMembers(CSharpDecompiler decompiler, ITypeDefinition typeDefinition, ISymbol symbol)
    {
        var wantCtor = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor };
        var parameterCount = symbol switch
        {
            IMethodSymbol m => m.Parameters.Length,
            IPropertySymbol p => p.Parameters.Length,
            _ => -1,
        };

        // Prefer an exact match on the XML documentation id: it encodes the full signature
        // (parameter types, generic arity, indexer parameters) so overloads with the same name and
        // arity resolve to the one the caller actually asked for, not an arbitrary sibling.
        var docId = symbol.GetDocumentationCommentId();
        List<IMember> members;
        var exact = docId is null
            ? null
            : typeDefinition.Members.FirstOrDefault(m => IdMatches(m, docId));
        if (exact is not null)
        {
            members = [exact];
        }
        else
        {
            // Fallback (compiler-generated / IL-renamed members whose ids will not match): name and
            // parameter count, capped so an ambiguous match at least shows the candidates.
            members = typeDefinition.Members
                .Where(m => wantCtor
                    ? m is IMethod { IsConstructor: true }
                    : string.Equals(m.Name, symbol.MetadataName, StringComparison.Ordinal)
                      || string.Equals(m.Name, symbol.Name, StringComparison.Ordinal))
                .Where(m => parameterCount < 0
                            || m is not IParameterizedMember pm
                            || pm.Parameters.Count == parameterCount)
                .Take(3)
                .ToList();
        }

        if (members.Count == 0)
            throw new ToolException(
                $"Member '{symbol.Name}' not found in decompiled type {typeDefinition.FullName} "
                + "(it may be compiler-generated or renamed in IL). Decompile the whole type instead.");

        var sb = new StringBuilder();
        foreach (var member in members)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine(decompiler.DecompileAsString(member.MetadataToken));
        }
        return sb.ToString();
    }

    /// <summary>
    /// True when a decompiler member's XML documentation id equals the Roslyn symbol's. Both follow
    /// the ECMA-334 doc-id format, so this is an exact signature comparison.
    /// </summary>
    private static bool IdMatches(IEntity member, string docId)
    {
        try
        {
            return string.Equals(IdStringProvider.GetIdString(member), docId, StringComparison.Ordinal);
        }
        catch
        {
            // Some synthesized members throw while building an id; treat as no match.
            return false;
        }
    }

    /// <summary>The requested assembly, plus runtime-implementation fallbacks for reference assemblies.</summary>
    private static IEnumerable<string> CandidatePaths(string assemblyPath, bool isReferenceAssembly)
    {
        if (isReferenceAssembly)
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (runtimeDir is not null)
            {
                var simpleName = Path.GetFileName(assemblyPath);
                var implPath = Path.Combine(runtimeDir, simpleName);
                if (File.Exists(implPath))
                    yield return implPath;

                var coreLib = Path.Combine(runtimeDir, "System.Private.CoreLib.dll");
                if (File.Exists(coreLib))
                    yield return coreLib;
            }
        }

        yield return assemblyPath;
    }

    private static string ReflectionName(INamedTypeSymbol type)
    {
        var chain = new Stack<string>();
        for (var current = type; current is not null; current = current.ContainingType)
            chain.Push(current.MetadataName);

        var name = string.Join("+", chain);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } nsSymbol
            ? nsSymbol.ToDisplayString()
            : null;
        return ns is null ? name : $"{ns}.{name}";
    }
}
