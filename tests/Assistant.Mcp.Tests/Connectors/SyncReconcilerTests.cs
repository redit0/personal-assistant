using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Data;
using Assistant.Tools.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.Mcp.Tests.Connectors;

public sealed class SyncReconcilerTests : IAsyncLifetime
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private McpTestHost _host = null!;

    public async Task InitializeAsync() => _host = await McpTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Adds_new_items_once()
    {
        var snapshot = ConnectorSnapshot.Complete([Src("a"), Src("b")]);

        var first = await ApplyAsync(snapshot);
        var second = await ApplyAsync(snapshot);

        Assert.Equal(2, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(2, await _host.WithDbAsync(db => db.Items.CountAsync()));
    }

    [Fact]
    public async Task Updates_items_when_the_source_changes()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a", title: "Old title")]));
        var result = await ApplyAsync(ConnectorSnapshot.Complete([Src("a", title: "New title", updated: T0.AddHours(1))]));

        Assert.Equal(1, result.Updated);
        Assert.Equal("New title", await _host.WithDbAsync(db => db.Items.Select(i => i.Title).SingleAsync()));
    }

    [Fact]
    public async Task Closes_items_that_disappear_and_reopens_them_if_they_return()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a"), Src("b")]));

        var closedRound = await ApplyAsync(ConnectorSnapshot.Complete([Src("a")]));
        Assert.Equal(1, closedRound.Closed);
        var b = await GetAsync("test:b");
        Assert.Equal(ItemStatus.Done, b.Status);
        Assert.True(b.ClosedBySync);
        Assert.Contains(b.Notes, n => n.Text.StartsWith("Closed automatically"));

        var reopenedRound = await ApplyAsync(ConnectorSnapshot.Complete([Src("a"), Src("b", updated: T0.AddDays(1))]));
        Assert.Equal(1, reopenedRound.Reopened);
        Assert.Equal(ItemStatus.Open, (await GetAsync("test:b")).Status);
    }

    [Fact]
    public async Task Leaves_items_you_closed_yourself_alone()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a")]));
        await _host.WithDbAsync(async db =>
        {
            var item = await db.Items.SingleAsync();
            item.Status = ItemStatus.Dismissed;
            return await db.SaveChangesAsync();
        });

        var result = await ApplyAsync(ConnectorSnapshot.Complete([Src("a", updated: T0.AddDays(1))]));

        Assert.Equal(0, result.Reopened);
        Assert.Equal(ItemStatus.Dismissed, (await GetAsync("test:a")).Status);
    }

    [Fact]
    public async Task A_new_request_in_a_conversation_you_closed_becomes_a_new_item()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a", title: "Fix the chart") with { RequestStartedAt = DateTime.MinValue }]));
        await CloseItYourselfAsync(T0);

        var result = await ApplyAsync(ConnectorSnapshot.Complete([
            Src("a", title: "Write the questions", updated: T0.AddDays(1)) with { RequestStartedAt = T0.AddDays(1) }]));

        Assert.Equal(1, result.Added);
        var items = await _host.WithDbAsync(db => db.Items.OrderBy(i => i.Id).ToListAsync());
        Assert.Equal(2, items.Count);
        Assert.Equal(("Fix the chart", ItemStatus.Done), (items[0].Title, items[0].Status));
        Assert.Equal(("Write the questions", ItemStatus.Open), (items[1].Title, items[1].Status));
    }

    [Fact]
    public async Task A_later_message_in_a_conversation_you_closed_leaves_it_alone()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a", title: "Fix the chart") with { RequestStartedAt = DateTime.MinValue }]));
        await CloseItYourselfAsync(T0);

        var result = await ApplyAsync(ConnectorSnapshot.Complete([
            Src("a", title: "Reworded", updated: T0.AddDays(1)) with { RequestStartedAt = DateTime.MinValue }]));

        Assert.Equal(0, result.Added + result.Updated);
        Assert.Equal("Fix the chart", await _host.WithDbAsync(db => db.Items.Select(i => i.Title).SingleAsync()));
    }

    private Task<int> CloseItYourselfAsync(DateTime at) => _host.WithDbAsync(async db =>
    {
        var item = await db.Items.SingleAsync();
        item.Status = ItemStatus.Done;
        item.CompletedAt = at;
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Does_not_close_items_from_a_scope_that_failed_to_load()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a", scope: "org1"), Src("b", scope: "org2")]));

        var result = await ApplyAsync(new ConnectorSnapshot([Src("a", scope: "org1")], new HashSet<string> { "org2" }, []));

        Assert.Equal(0, result.Closed);
        Assert.Equal(ItemStatus.Open, (await GetAsync("org2:b")).Status);
    }

    [Fact]
    public async Task Ignores_items_from_other_sources()
    {
        await _host.WithDbAsync(async db =>
        {
            db.Items.Add(new Item { Title = "Manual", Source = "user", ExternalId = "test:a", CreatedAt = T0, UpdatedAt = T0 });
            return await db.SaveChangesAsync();
        });

        await ApplyAsync(ConnectorSnapshot.Complete([]));

        Assert.Equal(ItemStatus.Open, await _host.WithDbAsync(db => db.Items.Select(i => i.Status).SingleAsync()));
    }

    [Fact]
    public async Task Merges_a_new_item_into_the_open_item_it_repeats()
    {
        var emailId = await AddEmailItemAsync(dueAt: T0.AddDays(5));
        var detector = new FakeDetector(c => c.ExternalId == "test:slack" ? new DuplicateMatch(emailId, "Dana now needs it by Friday.", "Same report") : null);

        var result = await ApplyAsync(ConnectorSnapshot.Complete([
            Src("slack", title: "Send Dana the Q4 report") with { DueAt = T0.AddDays(2), Priority = Priority.High, Requester = "Dana", Trust = SenderTrust.External },
            Src("other")]), detector);

        Assert.Equal(1, result.Merged);
        Assert.Equal(1, result.Added);
        Assert.Equal(["Item other"], result.AddedTitles);

        // The second new item is checked against the first, too.
        Assert.Contains(detector.Seen, s => s.Candidate == "test:other" && s.OpenIds.Contains(emailId));

        var duplicate = await GetAsync("test:slack");
        Assert.Equal(ItemStatus.Merged, duplicate.Status);
        Assert.Equal(emailId, duplicate.MergedIntoId);

        var kept = await _host.WithDbAsync(db => db.Items.Include(i => i.Notes).Include(i => i.Artifacts).SingleAsync(i => i.Id == emailId));
        Assert.Equal(ItemStatus.Open, kept.Status);
        Assert.Equal(T0.AddDays(2), kept.DueAt);
        Assert.Equal(Priority.High, kept.Priority);
        Assert.Equal(SenderTrust.External, kept.Trust);
        Assert.Contains(kept.Notes, n => n.Text.Contains("Merged in the same request from Test") && n.Text.Contains("Dana now needs it by Friday."));
        Assert.Contains(kept.Artifacts, a => a.Kind == "duplicate" && a.Url == "https://example.com/slack");

        // The next sync updates the duplicate in place rather than creating it again.
        var again = await ApplyAsync(ConnectorSnapshot.Complete([Src("slack", title: "Send Dana the Q4 report"), Src("other")]), detector);
        Assert.Equal(0, again.Added + again.Merged);
        Assert.Equal(3, await _host.WithDbAsync(db => db.Items.CountAsync()));
    }

    [Fact]
    public async Task Resolving_a_merged_duplicate_closes_the_kept_item_and_its_return_reopens_it()
    {
        var emailId = await AddEmailItemAsync();
        var detector = new FakeDetector(_ => new DuplicateMatch(emailId, null, null));
        await ApplyAsync(ConnectorSnapshot.Complete([Src("slack")]), detector);

        var closed = await ApplyAsync(ConnectorSnapshot.Complete([]), detector);
        Assert.Equal(1, closed.Closed);
        var kept = await _host.WithDbAsync(db => db.Items.Include(i => i.Notes).SingleAsync(i => i.Id == emailId));
        Assert.Equal(ItemStatus.Done, kept.Status);
        Assert.Contains(kept.Notes, n => n.Text.Contains("same request was resolved in Test"));
        Assert.Equal(ItemStatus.Merged, (await GetAsync("test:slack")).Status);

        var reopened = await ApplyAsync(ConnectorSnapshot.Complete([Src("slack", updated: T0.AddDays(1))]), detector);
        Assert.Equal(1, reopened.Reopened);
        Assert.Equal(ItemStatus.Open, await _host.WithDbAsync(db => db.Items.Where(i => i.Id == emailId).Select(i => i.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_notification_about_work_just_finished_folds_into_it_and_stays_closed()
    {
        // The pull request merged moments before the email about it was triaged.
        var prId = await AddFinishedItemAsync(closedBySync: true, DateTime.UtcNow.AddMinutes(-1));
        var detector = new FakeDetector(_ => new DuplicateMatch(prId, null, "The same pull request, already merged."));

        var result = await ApplyAsync(ConnectorSnapshot.Complete([Src("mail", title: "Re-review the updated PR")]), detector);

        Assert.Equal((0, 1, 0), (result.Added, result.Merged, result.Reopened));
        Assert.Contains(detector.Seen, s => s.OpenIds.Contains(prId));
        Assert.Equal(ItemStatus.Merged, (await GetAsync("test:mail")).Status);
        var pr = await _host.WithDbAsync(db => db.Items.Include(i => i.Notes).SingleAsync(i => i.Id == prId));
        Assert.Equal(ItemStatus.Done, pr.Status);
        Assert.Contains(pr.Notes, n => n.Text.Contains("already done, so it stays closed"));

        // Later syncs leave both alone.
        var again = await ApplyAsync(ConnectorSnapshot.Complete([Src("mail", title: "Re-review the updated PR")]), detector);
        Assert.Equal(0, again.Added + again.Merged + again.Reopened);
        Assert.Equal(ItemStatus.Done, await _host.WithDbAsync(db => db.Items.Where(i => i.Id == prId).Select(i => i.Status).SingleAsync()));
    }

    [Fact]
    public async Task A_follow_up_showing_work_you_finished_isnt_done_reopens_it()
    {
        var doneId = await AddFinishedItemAsync(closedBySync: false, DateTime.UtcNow.AddHours(-3));
        var detector = new FakeDetector(_ => new DuplicateMatch(doneId, null, "Dana says the numbers are missing October.", Reopen: true));

        var result = await ApplyAsync(ConnectorSnapshot.Complete([Src("slack", title: "Q4 numbers are missing October")]), detector);

        Assert.Equal((1, 1), (result.Merged, result.Reopened));
        var kept = await _host.WithDbAsync(db => db.Items.Include(i => i.Notes).SingleAsync(i => i.Id == doneId));
        Assert.Equal(ItemStatus.Open, kept.Status);
        Assert.Null(kept.CompletedAt);
        Assert.Contains(kept.Notes, n => n.Text.StartsWith("Reopened:") && n.Text.Contains("missing October"));
    }

    [Fact]
    public async Task More_work_on_something_its_source_finished_becomes_its_own_item()
    {
        var prId = await AddFinishedItemAsync(closedBySync: true, DateTime.UtcNow.AddHours(-3));
        var detector = new FakeDetector(_ => new DuplicateMatch(prId, null, "The merged fix didn't work.", Reopen: true));

        var result = await ApplyAsync(ConnectorSnapshot.Complete([Src("mail", title: "The fix didn't work")]), detector);

        Assert.Equal((1, 0), (result.Added, result.Merged));
        var item = await GetAsync("test:mail");
        Assert.Equal(ItemStatus.Open, item.Status);
        Assert.Contains(item.Notes, n => n.Text.Contains($"Follows up on item {prId}"));
        Assert.Equal(ItemStatus.Done, await _host.WithDbAsync(db => db.Items.Where(i => i.Id == prId).Select(i => i.Status).SingleAsync()));
    }

    [Fact]
    public async Task Work_finished_long_ago_isnt_compared_against()
    {
        var oldId = await AddFinishedItemAsync(closedBySync: false, DateTime.UtcNow.AddDays(-30));
        var detector = new FakeDetector(_ => null);

        await ApplyAsync(ConnectorSnapshot.Complete([Src("slack")]), detector);

        Assert.DoesNotContain(detector.Seen, s => s.OpenIds.Contains(oldId));
    }

    [Fact]
    public async Task Triage_reopening_a_conversation_you_closed_reopens_the_same_item()
    {
        await ApplyAsync(ConnectorSnapshot.Complete([Src("a", title: "Fix the chart") with { RequestStartedAt = DateTime.MinValue }]));
        var closedAt = DateTime.UtcNow.AddHours(-1);
        await CloseItYourselfAsync(closedAt);

        var reopenedAt = DateTime.UtcNow;
        var conversation = Src("a", title: "Fix the chart axis", updated: T0.AddDays(1)) with
        {
            RequestStartedAt = DateTime.MinValue, ReopenedAt = reopenedAt, Reasoning = "Dana says it's still wrong.",
        };
        var result = await ApplyAsync(ConnectorSnapshot.Complete([conversation]));

        Assert.Equal((0, 1), (result.Added, result.Reopened));
        var item = await GetAsync("test:a");
        Assert.Equal((ItemStatus.Open, "Fix the chart axis"), (item.Status, item.Title));
        Assert.Contains(item.Notes, n => n.Text.Contains("show it isn't finished") && n.Text.Contains("still wrong"));

        // Close it again: the same reopen doesn't bring it back a second time.
        await CloseItYourselfAsync(DateTime.UtcNow.AddMinutes(1));
        var again = await ApplyAsync(ConnectorSnapshot.Complete([conversation with { UpdatedAt = T0.AddDays(2) }]));
        Assert.Equal(0, again.Reopened);
        Assert.Equal(ItemStatus.Done, (await GetAsync("test:a")).Status);
    }

    private Task<int> AddFinishedItemAsync(bool closedBySync, DateTime completedAt) => _host.WithDbAsync(async db =>
    {
        var item = new Item
        {
            Title = "Send Dana the Q4 numbers", Source = "azuredevops", ExternalId = "org:pr:1", Status = ItemStatus.Done,
            ClosedBySync = closedBySync, CompletedAt = completedAt, CreatedAt = T0, UpdatedAt = T0,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    });

    [Fact]
    public async Task Merged_items_are_hidden_and_can_be_split_back_out()
    {
        var emailId = await AddEmailItemAsync();
        await ApplyAsync(ConnectorSnapshot.Complete([Src("slack")]), new FakeDetector(_ => new DuplicateMatch(emailId, null, null)));
        var duplicateId = (await GetAsync("test:slack")).Id;

        await using var client = await _host.ConnectAsync();
        var list = await client.CallToolAsync("list_items", new Dictionary<string, object?> { ["status"] = "All" });
        Assert.DoesNotContain($"\"id\":{duplicateId},", Text(list));

        var detail = await client.CallToolAsync("get_item", new Dictionary<string, object?> { ["itemId"] = emailId });
        Assert.Contains("\"mergedFrom\":[{\"id\":" + duplicateId, Text(detail));

        var done = await client.CallToolAsync("mark_done", new Dictionary<string, object?> { ["itemId"] = duplicateId });
        Assert.True(done.IsError);

        var split = await client.CallToolAsync("split_item", new Dictionary<string, object?> { ["itemId"] = duplicateId });
        Assert.False(split.IsError, Text(split));
        var item = await GetAsync("test:slack");
        Assert.Equal(ItemStatus.Open, item.Status);
        Assert.Null(item.MergedIntoId);
        Assert.Empty(await _host.WithDbAsync(db => db.ItemArtifacts.Where(a => a.ItemId == emailId).ToListAsync()));
    }

    [Fact]
    public async Task Merge_items_tool_folds_one_item_into_another()
    {
        var emailId = await AddEmailItemAsync();
        await ApplyAsync(ConnectorSnapshot.Complete([Src("jira")]));
        var ticketId = (await GetAsync("test:jira")).Id;

        await using var client = await _host.ConnectAsync();
        var result = await client.CallToolAsync("merge_items", new Dictionary<string, object?> { ["itemId"] = emailId, ["intoItemId"] = ticketId });

        Assert.False(result.IsError, Text(result));
        Assert.Equal(ItemStatus.Merged, await _host.WithDbAsync(db => db.Items.Where(i => i.Id == emailId).Select(i => i.Status).SingleAsync()));
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text));

    private Task<int> AddEmailItemAsync(DateTime? dueAt = null) => _host.WithDbAsync(async db =>
    {
        var item = new Item
        {
            Title = "Send Dana the Q4 report", Source = "outlook", ExternalId = "mail:conv1", SourceUrl = "https://outlook.office.com/mail/1",
            Requester = "Dana", Trust = SenderTrust.Organization, DueAt = dueAt, CreatedAt = T0, UpdatedAt = T0,
        };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    });

    private sealed class FakeDetector(Func<SourceItem, DuplicateMatch?> decide) : IDuplicateDetector
    {
        public List<(string Candidate, int[] OpenIds)> Seen { get; } = [];

        public Task<DuplicateMatch?> FindAsync(SourceItem candidate, string sourceName, IReadOnlyList<Item> openItems, CancellationToken cancellationToken)
        {
            Seen.Add((candidate.ExternalId, [.. openItems.Select(i => i.Id)]));
            return Task.FromResult(decide(candidate));
        }
    }

    private static SourceItem Src(string id, string? title = null, DateTime? updated = null, string scope = "test") =>
        new(scope, $"{scope}:{id}", title ?? $"Item {id}", ItemType.Review, $"https://example.com/{id}", updated ?? T0);

    private async Task<SyncResult> ApplyAsync(ConnectorSnapshot snapshot, IDuplicateDetector? detector = null)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var reconciler = detector is null
            ? ActivatorUtilities.CreateInstance<SyncReconciler>(scope.ServiceProvider)
            : ActivatorUtilities.CreateInstance<SyncReconciler>(scope.ServiceProvider, detector);
        return await reconciler.ApplyAsync("test", snapshot, "Test");
    }

    private Task<Item> GetAsync(string externalId) =>
        _host.WithDbAsync(db => db.Items.Include(i => i.Notes).SingleAsync(i => i.ExternalId == externalId));
}
