using System.Collections.Concurrent;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
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
/// when one can be located.
/// </summary>
public sealed class DecompilerService(ILogger<DecompilerService> log)
{
    private const int MaxOutputChars = 40_000;

    private readonly ConcurrentDictionary<string, CSharpDecompiler> _decompilers = new(StringComparer.OrdinalIgnoreCase);

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
                var decompiler = _decompilers.GetOrAdd(candidate, CreateDecompiler);
                var typeDefinition = decompiler.TypeSystem.FindType(new FullTypeName(reflectionName)).GetDefinition();
                if (typeDefinition is null
                    || !string.Equals(typeDefinition.ParentModule?.MetadataFile?.FileName, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Type not defined in this module (facade/forwarder) — try the next candidate.
                }

                var code = symbol is INamedTypeSymbol
                    ? decompiler.DecompileTypeAsString(new FullTypeName(reflectionName))
                    : DecompileMembers(decompiler, typeDefinition, symbol);

                if (code.Length > MaxOutputChars)
                    code = code[..MaxOutputChars] + $"\n… truncated at {MaxOutputChars} chars.";

                var header = $"// decompiled from {candidate}\n";
                if (isReferenceAssembly && candidate.Equals(assemblyPath, StringComparison.OrdinalIgnoreCase))
                    header += "// NOTE: reference assembly — signatures are exact but method bodies are stubs.\n";
                return header + code;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not ToolException)
            {
                log.LogDebug(ex, "Decompilation from {Path} failed; trying next candidate", candidate);
            }
        }

        throw new ToolException(
            $"Could not decompile {SymbolFormat.FqnOf(symbol)} from [{assemblySymbol.Name}]. "
            + "The defining module may be unavailable on this machine.");
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

        var members = typeDefinition.Members
            .Where(m => wantCtor
                ? m is IMethod { IsConstructor: true }
                : string.Equals(m.Name, symbol.MetadataName, StringComparison.Ordinal)
                  || string.Equals(m.Name, symbol.Name, StringComparison.Ordinal))
            .Where(m => parameterCount < 0
                        || m is not IParameterizedMember pm
                        || pm.Parameters.Count == parameterCount)
            .Take(3)
            .ToList();

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

                // Many facade types live in System.Private.Xml, System.Linq, etc. — try all runtime
                // assemblies whose simple name is a prefix match as a last resort? Too broad; the
                // two candidates above cover the overwhelmingly common cases.
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
