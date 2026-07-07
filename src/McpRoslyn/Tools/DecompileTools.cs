using System.ComponentModel;
using System.Text;
using McpRoslyn.Decompilation;
using McpRoslyn.Infrastructure;
using McpRoslyn.Symbols;
using McpRoslyn.Workspace;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpRoslyn.Tools;

[McpServerToolType]
public static class DecompileTools
{
    [McpServerTool(Name = "decompile")]
    [Description("Show the source of a NuGet/BCL symbol the solution references — decompiled on demand (metadata-as-source). Use this instead of guessing what a library API does; grep cannot see inside assemblies. For symbols declared in the solution it returns their real source.")]
    public static Task<string> Decompile(
        RoslynWorkspaceService workspace,
        DecompilerService decompiler,
        ILoggerFactory loggerFactory,
        CancellationToken ct,
        [Description("Type or member, e.g. 'System.Text.Json.JsonSerializer.Serialize' or 'Newtonsoft.Json.JsonConvert'.")]
        string symbol)
        => ToolRunner.Run(loggerFactory.CreateLogger("mcp-roslyn"), "decompile", async () =>
        {
            var solution = await workspace.GetSolutionAsync(ct);
            var resolved = await SymbolResolver.ResolveOrThrowAsync(solution, symbol, ct);
            var target = resolved.Symbol;

            // Symbols with source in the solution: return the real thing.
            if (target.DeclaringSyntaxReferences.Length > 0)
            {
                var sb = new StringBuilder();
                foreach (var syntaxRef in target.DeclaringSyntaxReferences.Take(3))
                {
                    var node = await syntaxRef.GetSyntaxAsync(ct);
                    var lineSpan = node.GetLocation().GetLineSpan();
                    sb.AppendLine($"// source (not decompiled): {workspace.RelPath(lineSpan.Path)}:{lineSpan.StartLinePosition.Line + 1}");
                    var text = node.ToString();
                    if (text.Length > 20_000)
                        text = text[..20_000] + "\n… truncated — read the file for the rest.";
                    sb.AppendLine(text);
                }
                return ToolHelpers.WithStaleNotice(sb.ToString().TrimEnd(), workspace);
            }

            return ToolHelpers.WithStaleNotice(await decompiler.DecompileAsync(resolved, ct), workspace);
        });
}
