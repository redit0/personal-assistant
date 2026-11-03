namespace Assistant.Tools.Catalog;

/// <summary>Who is calling a tool.</summary>
/// <param name="Id">Stable identifier recorded in the audit log and as an item's source, such as "chat" or "mcp:claude-code".</param>
/// <param name="DisplayName">Shown in approval prompts, such as "Claude Code".</param>
/// <param name="HandoffItemId">The item you handed this session with "Hand to Claude", if it is one. See <see cref="HandoffRegistry"/>.</param>
public sealed record ToolCaller(string Id, string DisplayName, int? HandoffItemId = null)
{
    public ToolCaller ForHandoff(int itemId) => this with { DisplayName = $"{DisplayName} (handling item {itemId})", HandoffItemId = itemId };

    public static ToolCaller Chat { get; } = new("chat", "Assistant chat");

    public static ToolCaller User { get; } = new("user", "You");

    public static ToolCaller System { get; } = new("system", "Personal Assistant");

    public static ToolCaller Mcp(string? clientName, string? clientTitle = null)
    {
        var name = string.IsNullOrWhiteSpace(clientName) ? "unknown-client" : clientName.Trim();
        return new ToolCaller($"mcp:{name}", string.IsNullOrWhiteSpace(clientTitle) ? name : clientTitle);
    }
}

/// <summary>Scoped per tool call so tool implementations know who called them.</summary>
public sealed class ToolCallContext
{
    public ToolCaller Caller { get; set; } = ToolCaller.System;
}
