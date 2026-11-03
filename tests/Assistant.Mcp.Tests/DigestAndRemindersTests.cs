using Assistant.Agent;
using Assistant.Agent.Calendar;
using Assistant.Agent.Digest;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Items;
using Assistant.Core.Notifications;
using Assistant.Core.Settings;
using Assistant.Tools.Reminders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assistant.Mcp.Tests;

public sealed class DigestAndRemindersTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly CapturingNotifier _notifier = new();
    private UserSettingsStore _settings = null!;
    private McpTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _settings = new UserSettingsStore(_settingsFile);
        _host = await McpTestHost.StartAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Remind_me_creates_a_task_or_attaches_to_an_item()
    {
        await using var client = await _host.ConnectAsync();
        var at = DateTimeOffset.Now.AddDays(1);

        var created = await client.CallToolAsync("remind_me", new Dictionary<string, object?> { ["at"] = at.ToString("O"), ["title"] = "Follow up with Dana" });
        Assert.False(created.IsError, Text(created));

        var waitingId = await AddItemAsync("Dana to send the numbers", ItemType.WaitingOn, ItemStatus.Open);
        var attached = await client.CallToolAsync("remind_me", new Dictionary<string, object?>
        {
            ["at"] = at.ToString("O"), ["itemId"] = waitingId, ["note"] = "Only if she hasn't replied",
        });
        Assert.False(attached.IsError, Text(attached));

        var past = await client.CallToolAsync("remind_me", new Dictionary<string, object?> { ["at"] = DateTimeOffset.Now.AddHours(-1).ToString("O"), ["title"] = "x" });
        Assert.True(past.IsError);

        var items = await _host.WithDbAsync(db => db.Items.Where(i => i.RemindAt != null).ToListAsync());
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Title == "Follow up with Dana" && i.Type == ItemType.Task && i.DueAt == i.RemindAt);
        Assert.Contains(items, i => i.Id == waitingId && i.ReminderNote == "Only if she hasn't replied");
    }

    [Fact]
    public async Task Reminders_fire_once_and_are_skipped_when_the_item_is_already_closed()
    {
        var due = DateTime.UtcNow.AddMinutes(-1);
        var openId = await AddItemAsync("Follow up with Dana", ItemType.WaitingOn, ItemStatus.Open, remindAt: due);
        var closedId = await AddItemAsync("Sam replied already", ItemType.WaitingOn, ItemStatus.Done, remindAt: due);

        var service = new ReminderService(_host.Services.GetRequiredService<IServiceScopeFactory>(), _notifier, _settings, TimeProvider.System, NullLogger<ReminderService>.Instance);
        await service.RunOnceAsync(default);
        await service.RunOnceAsync(default);

        var shown = Assert.Single(_notifier.Shown);
        Assert.Equal("Reminder: Follow up with Dana", shown.Title);
        Assert.Equal(openId, shown.ItemId);
        Assert.Equal(0, await _host.WithDbAsync(db => db.Items.CountAsync(i => i.RemindAt != null)));
        Assert.True(closedId > 0);
    }

    [Fact]
    public void Free_time_respects_working_hours_and_merges_overlapping_meetings()
    {
        // A Monday in a fixed zone, so the test doesn't depend on the machine's time zone.
        var zoneId = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time").Id;
        var hours = new WorkingHours(new HashSet<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday }, new TimeOnly(9, 0), new TimeOnly(17, 0), zoneId);
        DateTime Utc(int day, int hour, int minute = 0) =>
            TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Unspecified), hours.Zone);

        var events = new[]
        {
            Event(Utc(2, 9), Utc(2, 10)),
            Event(Utc(2, 9, 30), Utc(2, 11)),                    // overlaps the first
            Event(Utc(2, 13), Utc(2, 14), showAs: "free"),         // doesn't block
            Event(Utc(2, 15), Utc(2, 16, 30)),
            Event(Utc(3, 9), Utc(3, 17), response: "declined"),    // declined, doesn't block
        };

        var slots = FreeTime.Find(events, Utc(2, 0), Utc(4, 0), TimeSpan.FromMinutes(60), hours);

        Assert.Equal(
            ["11:00-15:00 Mon", "09:00-17:00 Tue"],
            slots.Select(s => $"{s.Start:HH:mm}-{s.End:HH:mm} {s.Start:ddd}"));
    }

    [Theory]
    [InlineData("2026-11-02T07:59:00", false)] // Monday, before 8
    [InlineData("2026-11-02T08:00:00", true)]
    [InlineData("2026-11-07T09:00:00", false)] // Saturday
    public void Digest_is_due_after_the_chosen_time_on_weekdays(string local, bool expected)
    {
        var settings = new DigestSettings { Time = "08:00", WeekdaysOnly = true };
        Assert.Equal(expected, DigestService.IsDue(settings, new DateTimeOffset(DateTime.Parse(local), TimeSpan.FromHours(-8))));
    }

    [Fact]
    public async Task Digest_sorts_items_into_sections_and_caches_the_day()
    {
        _settings.Update(s => s.Digest.AiSummary = false);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var noon = TimeZoneInfo.ConvertTimeToUtc(today.ToDateTime(new TimeOnly(12, 0)), TimeZoneInfo.Local);

        await AddItemAsync("Send the deck", ItemType.Ask, ItemStatus.Open, due: noon);
        await AddItemAsync("Prep: quarterly review", ItemType.Event, ItemStatus.Open, due: noon);
        await AddItemAsync("Old invoice", ItemType.Task, ItemStatus.Open, due: noon.AddDays(-3));
        await AddItemAsync("Waiting on legal", ItemType.WaitingOn, ItemStatus.Open, created: now.AddDays(-10));
        await AddItemAsync("Already done", ItemType.Ask, ItemStatus.Done, due: noon);

        var builder = CreateDigestBuilder(new ScriptedSummary());
        var digest = await builder.GetAsync(today, refresh: false, default);

        Assert.Equal(["Send the deck"], digest.DueToday.Select(i => i.Title));
        Assert.Equal(["Prep: quarterly review"], digest.Prep.Select(i => i.Title));
        Assert.Equal(["Old invoice"], digest.Overdue.Select(i => i.Title));
        Assert.Equal(["Waiting on legal"], digest.StaleWaiting.Select(i => i.Title));
        Assert.Null(digest.Summary);
        Assert.Contains("2 due today, 1 overdue", digest.Headline);

        // The second read comes from the cache, including a round trip through JSON.
        await AddItemAsync("Added after the digest", ItemType.Ask, ItemStatus.Open, due: noon);
        var cached = await builder.GetAsync(today, refresh: false, default);
        Assert.Equal(digest.DueToday.Select(i => i.Id), cached.DueToday.Select(i => i.Id));
        Assert.Equal(digest.Date, cached.Date);

        var refreshed = await builder.GetAsync(today, refresh: true, default);
        Assert.Equal(2, refreshed.DueToday.Count);
    }

    [Fact]
    public async Task Digest_adds_an_ai_summary_when_enabled()
    {
        _settings.Update(s => s.Digest.AiSummary = true);
        await AddItemAsync("Send the deck", ItemType.Ask, ItemStatus.Open, due: DateTime.UtcNow.AddHours(1));
        var ai = new ScriptedSummary();

        var digest = await CreateDigestBuilder(ai).GetAsync(DateOnly.FromDateTime(DateTime.Now), refresh: true, default);

        Assert.Equal("A focused day.\n\nTop priorities:\n- Send the deck (#1)", digest.Summary);
        Assert.Contains("Send the deck", ai.LastPrompt);
        Assert.Equal(ReasoningEffort.Low, ai.LastOptions?.Reasoning?.Effort);
    }

    private DigestBuilder CreateDigestBuilder(ITriageClientSource ai) => new(
        _host.Services.GetRequiredService<IServiceScopeFactory>(),
        new GraphClient(new Connectors.FakeHttp(), new NoMicrosoft()),
        new NoMicrosoft(),
        ai,
        _settings,
        TimeProvider.System,
        NullLogger<DigestBuilder>.Instance);

    private Task<int> AddItemAsync(string title, ItemType type, ItemStatus status, DateTime? due = null, DateTime? remindAt = null, DateTime? created = null) =>
        _host.WithDbAsync(async db =>
        {
            var item = new Item
            {
                Title = title, Type = type, Status = status, DueAt = due, RemindAt = remindAt, Source = "test",
                CreatedAt = created ?? DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });

    private static GraphEvent Event(DateTime start, DateTime end, string? showAs = "busy", string? response = "accepted") =>
        new("e", "Meeting", start, end, false, false, false, null, [], null, null, null, response, DateTime.UtcNow, showAs);

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private sealed class CapturingNotifier : INotifier
    {
        public List<(string Title, string Message, int? ItemId)> Shown { get; } = [];

        public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, int? itemId = null) =>
            Shown.Add((title, message, itemId));
    }

    private sealed class NoMicrosoft : IMicrosoftTokenProvider
    {
        public bool IsConfigured => false;

        public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not signed in.");
    }

    private sealed class ScriptedSummary : IChatClient, ITriageClientSource
    {
        public string? LastPrompt { get; private set; }

        public ChatOptions? LastOptions { get; private set; }

        public IChatClient? GetTriageClient() => this;

        public string? UnavailableReason(AiSettings? ai = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Join("\n", messages.Select(m => m.Text));
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "A focused day.\n\nTop priorities:\n- Send the deck (#1)")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
