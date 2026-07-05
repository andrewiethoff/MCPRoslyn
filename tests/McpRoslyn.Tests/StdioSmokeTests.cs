using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpRoslyn.Tests;

/// <summary>End-to-end: spawn the real server over stdio and speak MCP to it.</summary>
public class StdioSmokeTests
{
    [Fact]
    public async Task ServerSpeaksMcp_ListsAllTools_AndAnswersPing()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "McpRoslyn.dll");
        Assert.True(File.Exists(serverDll), $"server dll not found at {serverDll}");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "mcp-roslyn-under-test",
            Command = "dotnet",
            Arguments = [serverDll],
            EnvironmentVariables = new Dictionary<string, string?> { ["MCPROSLYN_AUTOLOAD"] = "0" },
        });

        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        Assert.Equal(16, tools.Count);
        Assert.Contains(tools, t => t.Name == "find_references");
        Assert.Contains(tools, t => t.Name == "decompile");
        Assert.Contains(tools, t => t.Name == "analyze_impact");

        var result = await client.CallToolAsync("ping", new Dictionary<string, object?>());
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.Contains("mcp-roslyn v", text);
        Assert.Contains("no solution loaded", text);
    }
}
