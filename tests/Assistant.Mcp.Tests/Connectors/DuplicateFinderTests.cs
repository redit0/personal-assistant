using Assistant.Agent.Triage;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Mcp.Tests.Connectors;

public sealed class DuplicateFinderTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly OutlookTriageTests.ScriptedChatClient _ai = new();
    private McpTestHost _host = null!;
    private DuplicateFinder _finder = null!;

    private static readonly List<Item> Open =
    [
        new() { Id = 7, Title = "Send Dana the Q4 report", Source = "outlook", Requester = "Dana", Details = "Dana needs the Q4 report for the board.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        new() { Id = 9, Title = "Fix the login bug on the Sansum site", Source = "jira", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
    ];

    private static readonly SourceItem Candidate = new(
        "slack", "C9:1", "Send the Q4 report to Dana", ItemType.Ask, "https://example.slack.com/archives/C9/p1", DateTime.UtcNow,
        Requester: "Dana", SourceContent: "hey, any update on the Q4 report? board moved to Friday");

    public async Task InitializeAsync()
    {
        _host = await McpTestHost.StartAsync();
        var triage = new TriageService(_ai, new UserSettingsStore(_settingsFile), _host.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        _finder = new DuplicateFinder(triage, NullLogger<DuplicateFinder>.Instance);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Returns_the_open_item_the_model_names()
    {
        _ai.Respond = _ => """{"duplicateOf":7,"confidence":0.9,"addedContext":"The board meeting moved to Friday.","reasoning":"Same report for Dana."}""";

        var match = await _finder.FindAsync(Candidate, "Slack", Open, default);

        Assert.Equal(7, match?.ItemId);
        Assert.Equal("The board meeting moved to Friday.", match?.AddedContext);
        var prompt = Assert.Single(_ai.Prompts);
        Assert.Contains("[7] Send Dana the Q4 report", prompt);
        Assert.Contains("[9] Fix the login bug", prompt);
        Assert.Contains("<untrusted_content", prompt);
    }

    [Theory]
    [InlineData("""{"duplicateOf":7,"confidence":0.5}""")]
    [InlineData("""{"duplicateOf":42,"confidence":0.95}""")]
    [InlineData("""{"duplicateOf":null,"confidence":0.9}""")]
    [InlineData("not json")]
    public async Task Keeps_the_item_separate_when_the_answer_is_unsure_or_unusable(string answer)
    {
        _ai.Respond = _ => answer;

        Assert.Null(await _finder.FindAsync(Candidate, "Slack", Open, default));
    }

    [Fact]
    public async Task Shows_finished_items_with_how_they_closed_and_passes_on_reopen()
    {
        var merged = new Item
        {
            Id = 12, Title = "Review: Preserve external URLs", Source = "azuredevops", Status = ItemStatus.Done, ClosedBySync = true,
            CompletedAt = DateTime.UtcNow.AddMinutes(-1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            Notes = [new ItemNote { Text = "Closed automatically: it's no longer open or waiting on you in Azure DevOps.", Author = "azuredevops", CreatedAt = DateTime.UtcNow }],
        };
        var yours = new Item
        {
            Id = 13, Title = "Send Dana the Q3 report", Source = "outlook", Status = ItemStatus.Done,
            CompletedAt = DateTime.UtcNow.AddDays(-2), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var candidate = Candidate with { LatestMessageAt = DateTime.UtcNow.AddMinutes(-5) };

        _ai.Respond = _ => """{"duplicateOf":12,"confidence":0.9,"reopen":true,"reasoning":"Asks for more on the same change."}""";
        var reopen = await _finder.FindAsync(candidate, "Outlook", [.. Open, merged, yours], default);

        Assert.Equal((12, true), (reopen?.ItemId, reopen?.Reopen));
        var prompt = _ai.Prompts[^1];
        Assert.Contains("Its latest message was sent", prompt);
        Assert.Contains("Items finished lately:", prompt);
        Assert.Contains("[12] Review: Preserve external URLs", prompt);
        Assert.Contains("no longer open or waiting on you in Azure DevOps", prompt);
        Assert.Contains("Marked done by the user.", prompt);

        // Reopen means nothing for an item that's still open.
        _ai.Respond = _ => """{"duplicateOf":7,"confidence":0.9,"reopen":true}""";
        Assert.False((await _finder.FindAsync(candidate, "Outlook", [.. Open, merged], default))?.Reopen);
    }

    [Fact]
    public async Task Doesnt_ask_when_nothing_is_open()
    {
        Assert.Null(await _finder.FindAsync(Candidate, "Slack", [], default));
        Assert.Empty(_ai.Prompts);
    }
}
