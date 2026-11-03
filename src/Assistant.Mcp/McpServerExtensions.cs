using System.Reflection;
using Assistant.Tools.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assistant.Mcp;

public static class McpServerExtensions
{
    public const string ServerName = "personal-assistant";

    private const string Instructions = Core.Agents.AgentGuidance.McpServerInstructions;

    /// <summary>
    /// Adds the MCP server. Tools come from <see cref="ToolCatalog"/> (register it with AddAssistantTools)
    /// and every call goes through <see cref="ToolInvoker"/>.
    /// </summary>
    public static IServiceCollection AddAssistantMcp(this IServiceCollection services, Action<AssistantMcpOptions>? configure = null)
    {
        services.AddOptions<AssistantMcpOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddSingleton<McpTokenProvider>();
        services.AddHttpContextAccessor();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Title = "Personal Assistant",
                    Version = typeof(McpServerExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion.Split('+')[0] ?? "0.0.0",
                };
                options.ServerInstructions = Instructions;
            })
            .WithHttpTransport()
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync)
            .WithListPromptsHandler(McpPrompts.ListAsync)
            .WithGetPromptHandler(McpPrompts.GetAsync);

        return services;
    }

    /// <summary>Maps the MCP endpoint behind <see cref="McpSecurityMiddleware"/>.</summary>
    public static IEndpointConventionBuilder MapAssistantMcp(this WebApplication app)
    {
        var path = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AssistantMcpOptions>>().Value.Path;
        app.UseMiddleware<McpSecurityMiddleware>();
        return app.MapMcp(path);
    }

    public static Tool ToProtocolTool(ToolDefinition tool) => new()
    {
        Name = tool.Name,
        Title = tool.Title,
        Description = tool.Description,
        InputSchema = tool.Function.JsonSchema,
        Annotations = new ToolAnnotations
        {
            Title = tool.Title,
            ReadOnlyHint = tool.ReadOnly,
            DestructiveHint = tool.ReadOnly ? null : tool.Destructive,
            IdempotentHint = tool.ReadOnly ? null : tool.Idempotent,
            OpenWorldHint = tool.OpenWorld,
        },
    };

    private static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        var catalog = request.Services!.GetRequiredService<ToolCatalog>();
        return ValueTask.FromResult(new ListToolsResult { Tools = [.. catalog.Tools.Select(ToProtocolTool)] });
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var invoker = request.Services!.GetRequiredService<ToolInvoker>();
        var client = request.Server.ClientInfo;

        // Clients on older protocol revisions only identify themselves in the initialize call, which a
        // stateless server doesn't keep. Fall back to the User-Agent so the audit log still says who called.
        var http = request.Services!.GetService<IHttpContextAccessor>()?.HttpContext;
        var name = client?.Name ?? UserAgentProduct(http);
        var caller = ToolCaller.Mcp(name, client?.Title);
        if (http?.Items[McpSecurityMiddleware.HandoffItemKey] is int handoffItemId)
        {
            caller = caller.ForHandoff(handoffItemId);
        }

        var arguments = request.Params?.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value)
            ?? new Dictionary<string, object?>();

        var result = await invoker.InvokeAsync(request.Params?.Name ?? string.Empty, arguments, caller, cancellationToken);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = result.ToJson() }],
            IsError = !result.Success,
        };
    }

    /// <summary>"claude-code" from "claude-code/2.1.3 (external, cli)".</summary>
    private static string? UserAgentProduct(HttpContext? http)
    {
        var userAgent = http?.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        var product = userAgent.Split(' ', 2)[0].Split('/', 2)[0].Trim();
        return product.Length is > 0 and <= 64 ? product : null;
    }
}
