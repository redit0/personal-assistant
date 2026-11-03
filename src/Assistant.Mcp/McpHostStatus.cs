namespace Assistant.Mcp;

/// <summary>Whether the MCP endpoint is listening, for the Settings page and tray tooltip.</summary>
public sealed class McpHostStatus
{
    public bool IsRunning { get; set; }

    public int Port { get; set; }

    public string? Error { get; set; }

    public string Endpoint => McpClientConfig.Endpoint(Port);
}
