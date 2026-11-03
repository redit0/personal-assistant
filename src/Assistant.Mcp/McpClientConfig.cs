using System.Text.Json;

namespace Assistant.Mcp;

/// <summary>Copy-and-paste configuration for connecting MCP clients to this server.</summary>
public static class McpClientConfig
{
    public static string Endpoint(int port) => $"http://127.0.0.1:{port}/mcp";

    public static string ClaudeCode(int port, string token) =>
        $"claude mcp add --transport http --scope user {McpServerExtensions.ServerName} {Endpoint(port)} " +
        $"--header \"Authorization: Bearer {token}\"";

    /// <summary>
    /// The --mcp-config file for a "Hand to Claude" session. It replaces your registered server of the same name
    /// for that session only, adding the handoff key so the assistant knows which item the session is handling.
    /// </summary>
    public static string ClaudeCodeHandoff(int port, string token, string handoffKey) => JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [McpServerExtensions.ServerName] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = Endpoint(port),
                    ["headers"] = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {token}",
                        [Tools.Catalog.HandoffRegistry.HeaderName] = handoffKey,
                    },
                },
            },
        },
        new JsonSerializerOptions { WriteIndented = true });

    public static string VsCode(int port, string token) => JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            ["servers"] = new Dictionary<string, object>
            {
                [McpServerExtensions.ServerName] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = Endpoint(port),
                    ["headers"] = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
                },
            },
        },
        new JsonSerializerOptions { WriteIndented = true });
}
