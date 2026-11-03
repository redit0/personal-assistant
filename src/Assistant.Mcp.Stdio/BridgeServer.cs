using System.Reflection;
using Assistant.Core.Agents;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace Assistant.Mcp.Stdio;

public static class BridgeServer
{
    /// <summary>
    /// Adds an MCP server that forwards tools/list and tools/call to the tray app. Register an
    /// <see cref="UpstreamConnection"/> alongside it. The caller picks the transport (stdio in production).
    /// </summary>
    public static IMcpServerBuilder AddBridgeServer(this IServiceCollection services) =>
        services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "personal-assistant",
                    Title = "Personal Assistant",
                    Version = typeof(BridgeServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion.Split('+')[0] ?? "0.0.0",
                };
                options.ServerInstructions = AgentGuidance.McpServerInstructions;
            })
            .WithListToolsHandler((request, ct) =>
                new ValueTask<ListToolsResult>(request.Services!.GetRequiredService<UpstreamConnection>()
                    .ListToolsAsync(request.Server.ClientInfo, ct)))
            .WithCallToolHandler((request, ct) =>
                new ValueTask<CallToolResult>(request.Services!.GetRequiredService<UpstreamConnection>()
                    .CallToolAsync(request.Params!, request.Server.ClientInfo, ct)))
            .WithListPromptsHandler((request, ct) =>
                new ValueTask<ListPromptsResult>(request.Services!.GetRequiredService<UpstreamConnection>()
                    .ListPromptsAsync(request.Server.ClientInfo, ct)))
            .WithGetPromptHandler((request, ct) =>
                new ValueTask<GetPromptResult>(request.Services!.GetRequiredService<UpstreamConnection>()
                    .GetPromptAsync(request.Params!, request.Server.ClientInfo, ct)));
}
