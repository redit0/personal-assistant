namespace Assistant.Web.Services;

/// <summary>A filter chip on the Items page. Several item sources can share one group.</summary>
public sealed record SourceGroup(string Key, string Label, int Order);

/// <summary>Groups item sources into the filters shown on the Items page.</summary>
public static class SourceGroups
{
    private static readonly Dictionary<string, SourceGroup> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["outlook"] = new("outlook", "Email", 0),
        ["outlook-flagged"] = new("outlook-flagged", "Flagged", 1),
        ["calendar"] = new("calendar", "Calendar", 2),
        ["jira"] = new("jira", "Jira", 3),
        ["azuredevops"] = new("azuredevops", "Azure DevOps", 4),
        ["github"] = new("github", "GitHub", 5),
        ["bitbucket"] = new("bitbucket", "Bitbucket", 6),
        ["user"] = new("user", "Added by me", 7),
        ["chat"] = new("chat", "Chat", 8),
    };

    private static readonly SourceGroup Agents = new("agents", "Agents", 9);

    public static SourceGroup Of(string source)
    {
        if (Known.TryGetValue(source, out var group))
        {
            return group;
        }

        // Every MCP client (mcp:claude-code, mcp:claude-ai, ...) shares one chip.
        return source.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase)
            ? Agents
            : new SourceGroup(source, source, 99);
    }
}
