using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Core.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Tools.Catalog;

/// <summary>Marks a method as a tool in the catalog. Use <see cref="System.ComponentModel.DescriptionAttribute"/> for the description.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AssistantToolAttribute(string name, ToolCategory category) : Attribute
{
    public string Name { get; } = name;

    public ToolCategory Category { get; } = category;

    public required string Title { get; init; }

    /// <summary>Can the call destroy or overwrite something? Only meaningful for non-read tools.</summary>
    public bool Destructive { get; init; }

    /// <summary>Does calling it twice with the same arguments have no extra effect?</summary>
    public bool Idempotent { get; init; }

    /// <summary>Does it reach systems outside the assistant (GitHub, Outlook, Slack)?</summary>
    public bool OpenWorld { get; init; }

    /// <summary>Always asks for approval, even when its policy is set to Allow. Deny still blocks it.</summary>
    public bool AlwaysAsk { get; init; }
}

/// <summary>A tool in the catalog. <see cref="Function"/> is the raw, unguarded function: call it through <see cref="ToolInvoker"/>.</summary>
public sealed record ToolDefinition(
    string Name,
    string Title,
    ToolCategory Category,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld,
    AIFunction Function,
    bool AlwaysAsk = false)
{
    public string Description => Function.Description;

    public bool ReadOnly => Category == ToolCategory.Read;

    public ToolPolicy DefaultPolicy => Category switch
    {
        ToolCategory.SourceAction => ToolPolicy.Ask,
        _ => ToolPolicy.Allow,
    };
}

/// <summary>
/// The single list of tools. The tray chat agent and the MCP server both expose exactly this set,
/// and both go through <see cref="ToolInvoker"/> for policy checks, approvals, and auditing.
/// </summary>
public sealed class ToolCatalog
{
    private readonly Dictionary<string, ToolDefinition> _byName;

    public ToolCatalog(IEnumerable<ToolDefinition> tools)
    {
        Tools = [.. tools.OrderBy(t => t.Name, StringComparer.Ordinal)];
        _byName = Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<ToolDefinition> Tools { get; }

    public ToolDefinition? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>Builds definitions from every <see cref="AssistantToolAttribute"/> method on the given types.</summary>
    public static IEnumerable<ToolDefinition> FromTypes(params Type[] types)
    {
        foreach (var type in types)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<AssistantToolAttribute>();
                if (attr is null)
                {
                    continue;
                }

                var function = AIFunctionFactory.Create(
                    method,
                    args => ActivatorUtilities.CreateInstance(
                        args.Services ?? throw new InvalidOperationException("Tools must be invoked through ToolInvoker."),
                        type),
                    new AIFunctionFactoryOptions { Name = attr.Name, SerializerOptions = ToolJson.Options });

                yield return new ToolDefinition(
                    attr.Name, attr.Title, attr.Category, attr.Destructive, attr.Idempotent, attr.OpenWorld, function, attr.AlwaysAsk);
            }
        }
    }
}

public static class ToolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>A tool error meant for the caller to read, such as "Item 12 does not exist."</summary>
public sealed class ToolException(string message) : Exception(message);
