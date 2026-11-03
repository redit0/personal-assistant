using System.Text.Json;
using Assistant.Core.Agents;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Assistant.Mcp;

/// <summary>
/// Serves <see cref="PromptTemplates"/> to MCP clients. In Claude Code they show up as slash commands, such as
/// /personal-assistant:handle_item 142.
/// </summary>
public static class McpPrompts
{
    public static ValueTask<ListPromptsResult> ListAsync(RequestContext<ListPromptsRequestParams> request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ListPromptsResult
        {
            Prompts = [.. PromptTemplates.All.Select(p => new Prompt
            {
                Name = p.Name,
                Title = p.Title,
                Description = p.Description,
                Arguments = [.. p.Arguments.Select(a => new PromptArgument { Name = a.Name, Description = a.Description, Required = a.Required })],
            })],
        });

    public static ValueTask<GetPromptResult> GetAsync(RequestContext<GetPromptRequestParams> request, CancellationToken cancellationToken)
    {
        var template = PromptTemplates.Find(request.Params?.Name) ?? throw new McpException($"Unknown prompt '{request.Params?.Name}'.");

        var arguments = (request.Params?.Arguments ?? new Dictionary<string, JsonElement>())
            .ToDictionary(kv => kv.Key, kv => kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        string text;
        try
        {
            text = template.Render(arguments);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }

        return ValueTask.FromResult(new GetPromptResult
        {
            Description = template.Description,
            Messages = [new PromptMessage { Role = Role.User, Content = new TextContentBlock { Text = text } }],
        });
    }
}
