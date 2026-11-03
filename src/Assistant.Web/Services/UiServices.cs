using Assistant.Tools.Catalog;
using Assistant.Tools.Items;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Web.Services;

/// <summary>Copies text to the Windows clipboard. The tray app implements this.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>Whether the app starts when you sign in to Windows. The tray app implements this.</summary>
public interface IStartupService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}

/// <summary>Lets the tray menu send the window to a page.</summary>
public sealed class NavigationBridge
{
    public event Action<string>? NavigateRequested;

    public void NavigateTo(string path) => NavigateRequested?.Invoke(path);
}

/// <summary>
/// Item operations for the UI. Reuses the catalog's tool code so the UI and agents behave the same,
/// but skips policy checks and approvals because you are the one clicking.
/// </summary>
public sealed class ItemsUiService(IServiceScopeFactory scopes)
{
    public Task<IReadOnlyList<ItemSummary>> ListAsync(ItemListStatus status) =>
        RunAsync(t => t.ListItemsAsync(status, limit: 1000));

    public Task<IReadOnlyList<ItemSummary>> SearchAsync(string query, bool includeClosed) =>
        RunAsync(t => t.SearchAsync(query, includeClosed, limit: 1000));

    public Task<ItemDetail> GetAsync(int id) => RunAsync(t => t.GetItemAsync(id));

    public Task<ItemDetail> CreateAsync(string title, Core.Items.ItemType type, DateTimeOffset? dueAt, Core.Items.Priority priority, string? details) =>
        RunAsync(t => t.CreateItemAsync(title, type, details, dueAt, priority));

    public Task<ItemDetail> MarkDoneAsync(int id) => RunAsync(t => t.MarkDoneAsync(id));

    public Task<ItemDetail> DismissAsync(int id) => RunAsync(t => t.DismissItemAsync(id));

    public Task<ItemDetail> ReopenAsync(int id) => RunAsync(t => t.ReopenItemAsync(id));

    public Task<ItemDetail> SnoozeAsync(int id, DateTimeOffset until) => RunAsync(t => t.SnoozeAsync(id, until));

    public Task<ItemDetail> AddNoteAsync(int id, string text) => RunAsync(t => t.AddNoteAsync(id, text));

    public Task<ItemDetail> SplitAsync(int duplicateId) => RunAsync(t => t.SplitItemAsync(duplicateId));

    /// <summary>
    /// Records whether an AI-created item was worth having. Useful keeps it and clears Needs a look;
    /// not useful dismisses it. Either way, triage sees the feedback as an example next time.
    /// </summary>
    public async Task FeedbackAsync(int id, bool useful)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Data.AssistantDbContext>();
        var item = await db.Items.FindAsync(id) ?? throw new InvalidOperationException($"Item {id} does not exist.");
        var now = DateTime.UtcNow;

        db.ItemFeedback.Add(new Core.Signals.ItemFeedback
        {
            ItemId = id,
            Useful = useful,
            ItemTitle = item.Title,
            Requester = item.Requester,
            Source = item.Source,
            CreatedAt = now,
        });

        item.NeedsReview = false;
        if (!useful)
        {
            item.Status = Core.Items.ItemStatus.Dismissed;
            item.CompletedAt = now;
        }

        item.UpdatedAt = now;
        await db.SaveChangesAsync();
    }

    /// <summary>Sources whose items come from AI triage and can take feedback.</summary>
    public static bool IsTriaged(string source) => source is "outlook" or "calendar" or "teams" or "slack";

    private async Task<T> RunAsync<T>(Func<ItemTools, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ToolCallContext>().Caller = ToolCaller.User;
        return await action(ActivatorUtilities.CreateInstance<ItemTools>(scope.ServiceProvider));
    }
}

public static class WebServiceCollectionExtensions
{
    public static IServiceCollection AddAssistantWeb(this IServiceCollection services)
    {
        services.AddSingleton<NavigationBridge>();
        services.AddSingleton<ItemsUiService>();
        services.AddSingleton<ClaudeLauncher>();
        return services;
    }
}
