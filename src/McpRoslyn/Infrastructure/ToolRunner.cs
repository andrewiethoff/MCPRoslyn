using Microsoft.Extensions.Logging;

namespace McpRoslyn.Infrastructure;

public static class ToolRunner
{
    /// <summary>
    /// Wraps a tool body: <see cref="ToolException"/> becomes a readable "ERROR: ..." text,
    /// unexpected exceptions are logged to stderr and summarized. We deliberately return error
    /// text instead of throwing, because the MCP SDK replaces unknown exception messages with a
    /// generic one the agent cannot act on.
    /// </summary>
    public static async Task<string> Run(ILogger logger, string toolName, Func<Task<string>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (ToolException tex)
        {
            return "ERROR: " + tex.Message;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Tool {Tool} failed unexpectedly", toolName);
            return $"ERROR ({ex.GetType().Name}): {ex.Message}";
        }
    }
}
