namespace McpRoslyn.Infrastructure;

/// <summary>
/// A tool-level failure whose message is meant for the calling agent and always names the
/// recovery action. Rendered as plain "ERROR: ..." text (not an MCP protocol error) so the
/// agent can read and act on it.
/// </summary>
public sealed class ToolException(string message) : Exception(message);
