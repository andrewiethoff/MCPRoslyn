using McpRoslyn.Decompilation;
using McpRoslyn.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Shield stdout FIRST: stdout carries the JSON-RPC stream. The stdio transport holds the
// raw stdout stream (Console.OpenStandardOutput()), so redirecting Console.Out diverts any
// stray Console.WriteLine from MSBuild/Roslyn/analyzers to stderr instead of corrupting
// the protocol.
Console.SetOut(Console.Error);

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.AddSingleton<RoslynWorkspaceService>();
builder.Services.AddSingleton<DecompilerService>();
builder.Services.AddHostedService<StartupAutoLoader>();

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new()
        {
            Name = "mcp-roslyn",
            Version = typeof(RoslynWorkspaceService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        };
        o.ServerInstructions =
            "Read-only Roslyn code analysis for C#/VB.NET solutions. Use these tools instead of grep when you need " +
            "semantic exactness: find_references, find_implementations, call_hierarchy, get_diagnostics, or to look " +
            "inside NuGet/BCL assemblies (decompile). Symbols are addressed by (fuzzy) fully-qualified name, e.g. " +
            "'OrderService.Process' — never by line/column. A solution is auto-loaded from the working directory; " +
            "use load_solution to switch. Results reflect files on disk (auto-synced after edits).";
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

/// <summary>Kicks off solution auto-discovery + load in the background at startup.</summary>
internal sealed class StartupAutoLoader(RoslynWorkspaceService workspace, ILogger<StartupAutoLoader> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await workspace.AutoLoadOnStartupAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Startup auto-load failed; a solution can still be loaded via the load_solution tool.");
        }
    }
}
