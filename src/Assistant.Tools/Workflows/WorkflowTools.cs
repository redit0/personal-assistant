using System.ComponentModel;
using Assistant.Core.Agents;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Assistant.Tools.Items;
using Assistant.Tools.Repos;

namespace Assistant.Tools.Workflows;

public sealed record WorkflowStart(string Instructions, object Context);

/// <summary>
/// The MCP prompts as tools. Some clients (the Claude desktop app's Code tab and Cowork among them) don't
/// let you pick MCP prompts, but every client can call tools, so "handle item 136" works anywhere.
/// </summary>
public sealed class WorkflowTools(AssistantDbContext db, ToolCallContext context, TimeProvider time)
{
    [AssistantTool("handle_item", ToolCategory.Read, Title = "Start handling an item")]
    [Description("Call this when the user asks you to handle, work on, take care of, or act on one of their items. " +
        "Returns step-by-step instructions to follow, plus the item's work context (the request, who asked, the due date, " +
        "and the local clone to work in). Follow the instructions, starting by summarizing the plan and waiting for the user's go-ahead. " +
        UntrustedContent.Notice)]
    public async Task<WorkflowStart> HandleItemAsync(
        [Description("The item's ID.")] int itemId,
        CancellationToken cancellationToken = default)
    {
        var workContext = await new RepoTools(db).GetWorkContextAsync(itemId, cancellationToken);
        var instructions = PromptTemplates.HandleItem.Render(new Dictionary<string, string> { ["itemId"] = itemId.ToString() });
        return new WorkflowStart(
            "The work context for step 1 is already included below as context, so start at step 2.\n\n" + instructions,
            workContext);
    }

    [AssistantTool("plan_my_day", ToolCategory.Read, Title = "Plan my day")]
    [Description("Call this when the user asks what to work on, what's on their plate today, or to plan their day. " +
        "Returns instructions plus their active items and the ones that need a look.")]
    public async Task<WorkflowStart> PlanMyDayAsync(CancellationToken cancellationToken = default)
    {
        var items = new ItemTools(db, context, time);
        var active = await items.ListItemsAsync(ItemListStatus.Active, limit: 200, cancellationToken: cancellationToken);
        var review = await items.ListItemsAsync(ItemListStatus.NeedsReview, limit: 100, cancellationToken: cancellationToken);
        return new WorkflowStart(
            "The items for step 1 are already included below as context, so start at step 2.\n\n" + PromptTemplates.PlanMyDay.Render(new Dictionary<string, string>()),
            new { active, needsALook = review, now = time.GetLocalNow() });
    }
}
