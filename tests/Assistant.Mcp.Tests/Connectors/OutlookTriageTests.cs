using System.Runtime.CompilerServices;
using Assistant.Agent;
using Assistant.Agent.Triage;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Mcp.Tests.Connectors;

public sealed class OutlookTriageTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly FakeHttp _http = new();
    private readonly ScriptedChatClient _ai = new();
    private UserSettingsStore _settings = null!;
    private McpTestHost _host = null!;
    private ThreadTriageEngine? _engine;

    public async Task InitializeAsync()
    {
        _host = await McpTestHost.StartAsync();
        _settings = new UserSettingsStore(_settingsFile);
        _settings.Update(s =>
        {
            s.Outlook.MailEnabled = true;
            s.Triage.Profile = "I'm a consultant at Contoso.";
        });

        _http
            .OnUrl("v1.0/me?$select", """{"displayName":"Sam Rivera","mail":"sam@example.com","userPrincipalName":"sam@example.com"}""")
            .OnUrl("inbox/messages/delta?$select", $$"""
                {"value":[
                  {{Message("m1", "c1", "Q4 numbers", "Dana Smith", "dana@example.com", "Can you send me the Q4 numbers by Friday?")}},
                  {{Message("m2", "c2", "October deals", "Vendor News", "news@vendor.com", "Big savings this month")}},
                  {{Message("m3", "c3", "[repo] CI failed", "GitHub", "noreply@github.com", "Build failed")}}
                ],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=INBOX1"}
                """)
            .OnUrl("sentitems/messages/delta?$select", """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/sentitems/messages/delta?$deltatoken=SENT1"}""")
            .OnUrl("messages/m1?$select=internetMessageHeaders", """{"internetMessageHeaders":[{"name":"From","value":"dana@example.com"}]}""")
            .OnUrl("messages/m2?$select=internetMessageHeaders", """{"internetMessageHeaders":[{"name":"List-Unsubscribe","value":"<mailto:x>"}]}""")
            .OnUrl("deltatoken=INBOX1", """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=INBOX2"}""")
            .OnUrl("deltatoken=SENT1", """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/sentitems/messages/delta?$deltatoken=SENT2"}""")
            .OnUrl("deltatoken=INBOX2", """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=INBOX2"}""")
            .OnUrl("deltatoken=SENT2", """{"value":[],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/sentitems/messages/delta?$deltatoken=SENT2"}""");

        _ai.Respond = prompt => prompt.Contains("Here are the Q4 numbers")
            ? """{"decision":"resolved","confidence":0.9,"reasoning":"You sent the numbers."}"""
            : """{"decision":"ask","title":"Send Dana the Q4 numbers","summary":"Dana needs the Q4 numbers.","due":"2030-01-04T17:00:00-08:00","dueConfidence":0.8,"priority":"high","requester":"Dana Smith","confidence":0.85,"reasoning":"Direct request with a deadline."}""";
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Filters_noise_triages_the_rest_and_closes_when_resolved()
    {
        var connector = CreateConnector();

        // First sync: only Dana's thread reaches the model.
        var first = await connector.FetchAsync(default);

        Assert.Single(_ai.Prompts);
        Assert.Contains("Can you send me the Q4 numbers", _ai.Prompts[0]);
        Assert.Contains("<untrusted_content source=\"email\">", _ai.Prompts[0]);
        Assert.Contains("I'm a consultant at Contoso.", _ai.SystemPrompts[0]);

        var item = Assert.Single(first.Items);
        Assert.Equal(ItemType.Ask, item.Type);
        Assert.Equal("Send Dana the Q4 numbers", item.Title);
        Assert.Equal("Dana Smith", item.Requester);
        Assert.Equal(SenderTrust.Organization, item.Trust);
        Assert.Equal(Priority.High, item.Priority);
        Assert.Equal(new DateTime(2030, 1, 5, 1, 0, 0, DateTimeKind.Utc), item.DueAt);
        Assert.False(item.NeedsReview);

        var signals = await _host.WithDbAsync(db => db.Signals.ToDictionaryAsync(s => s.ExternalId));
        Assert.Equal("mailing list", signals["m2"].FilteredReason);
        Assert.Equal("automated sender", signals["m3"].FilteredReason);
        Assert.Null(signals["m1"].FilteredReason);

        // Second sync with nothing new: no model calls, the item stays.
        var second = await connector.FetchAsync(default);
        Assert.Single(_ai.Prompts);
        Assert.Single(second.Items);

        // You reply with the numbers: the thread is judged again and drops out, so its item will close.
        _http.OnUrl("deltatoken=SENT2", $$"""
            {"value":[{{Message("m4", "c1", "RE: Q4 numbers", "Sam Rivera", "sam@example.com", "Here are the Q4 numbers.")}}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/sentitems/messages/delta?$deltatoken=SENT3"}
            """);

        var third = await connector.FetchAsync(default);
        Assert.Equal(2, _ai.Prompts.Count);
        Assert.Contains("Sent by the user", _ai.Prompts[1]);
        Assert.Contains("Earlier you judged this thread as ask", _ai.Prompts[1]);
        Assert.Empty(third.Items);
        Assert.Equal(TriageOutcome.Resolved, await _host.WithDbAsync(db => db.TriageDecisions.Select(d => d.Outcome).SingleAsync()));
    }

    [Fact]
    public async Task A_reply_showing_work_you_marked_done_isnt_finished_reopens_it()
    {
        var connector = CreateConnector();
        var first = await connector.FetchAsync(default);
        var externalId = Assert.Single(first.Items).ExternalId;
        await _host.WithDbAsync(async db =>
        {
            db.Items.Add(new Item
            {
                Title = "Send Dana the Q4 numbers", Source = "outlook", ExternalId = externalId, Status = ItemStatus.Done,
                CompletedAt = DateTime.UtcNow.AddHours(-1), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        _ai.Respond = _ => """{"decision":"ask","title":"Send Dana the Q4 numbers","confidence":0.9,"reopen":true,"reasoning":"Dana says the numbers are missing October."}""";
        _http.OnUrl("messages/m5?$select=internetMessageHeaders", """{"internetMessageHeaders":[{"name":"From","value":"dana@example.com"}]}""");
        _http.OnUrl("deltatoken=INBOX1", $$"""
            {"value":[{{Message("m5", "c1", "RE: Q4 numbers", "Dana Smith", "dana@example.com", "These are missing October.")}}],
             "@odata.deltaLink":"https://graph.microsoft.com/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=INBOX3"}
            """);

        var second = await connector.FetchAsync(default);

        Assert.Contains("The user marked that item done on", _ai.Prompts[^1]);
        var item = Assert.Single(second.Items);
        Assert.Equal(externalId, item.ExternalId);
        Assert.NotNull(item.ReopenedAt);
        Assert.True(item.RequestStartedAt < item.ReopenedAt, "Reopening keeps the same request, so the same item comes back.");
    }

    [Fact]
    public async Task Unsure_judgments_go_to_needs_a_look()
    {
        _ai.Respond = _ => """{"decision":"ask","title":"Maybe review the deck","confidence":0.4}""";

        var snapshot = await CreateConnector().FetchAsync(default);

        Assert.True(Assert.Single(snapshot.Items).NeedsReview);
    }

    [Fact]
    public async Task A_model_failure_keeps_existing_items_and_reports_a_backlog()
    {
        _ai.Respond = _ => throw new HttpRequestException("rate limited");

        var snapshot = await CreateConnector().FetchAsync(default);

        Assert.Empty(snapshot.Items);
        Assert.Contains(snapshot.Warnings, w => w.Contains("Triage stopped early"));
        Assert.Contains(snapshot.Warnings, w => w.Contains("1 thread still waiting"));
    }

    [Fact]
    public async Task A_cut_off_answer_is_retried_then_set_aside_without_stopping_triage()
    {
        // The real failure: thinking used up the output budget and the JSON stopped mid-string.
        _ai.Respond = _ => """{"decision":"ask","title":"Send Dana the""";
        _ai.FinishReason = ChatFinishReason.Length;
        var connector = CreateConnector();

        var first = await connector.FetchAsync(default);
        Assert.Contains(first.Warnings, w => w.Contains("unusable answer and will be retried"));
        Assert.DoesNotContain(first.Warnings, w => w.Contains("stopped early"));
        Assert.Equal(0, await _host.WithDbAsync(db => db.TriageDecisions.CountAsync()));

        await connector.FetchAsync(default);
        var third = await connector.FetchAsync(default);

        // Third strike: it stops being retried until a new message arrives, and goes to Needs a look so a request
        // in it isn't lost.
        Assert.Equal(3, _ai.Prompts.Count);
        var item = Assert.Single(third.Items);
        Assert.True(item.NeedsReview);
        Assert.Equal("Check this conversation: Q4 numbers", item.Title);

        await connector.FetchAsync(default);
        Assert.Equal(3, _ai.Prompts.Count);
    }

    [Fact]
    public async Task A_refused_thread_goes_to_needs_a_look_instead_of_retrying()
    {
        _ai.Respond = _ => """{"decision":"ask","reason""";
        _ai.FinishReason = ChatFinishReason.ContentFilter;
        var connector = CreateConnector();

        var first = await connector.FetchAsync(default);
        await connector.FetchAsync(default);

        var item = Assert.Single(first.Items);
        Assert.True(item.NeedsReview);
        Assert.Equal("Check this conversation: Q4 numbers", item.Title);
        Assert.Single(_ai.Prompts); // Not asked again.
    }

    [Fact]
    public async Task Triage_leaves_room_for_thinking_in_the_output_budget()
    {
        await CreateConnector().FetchAsync(default);

        Assert.True(_ai.LastOptions?.MaxOutputTokens >= 16000);
        Assert.Equal(ReasoningEffort.Low, _ai.LastOptions?.Reasoning?.Effort);
    }

    [Theory]
    [InlineData("noreply@github.com", "automated sender")]
    [InlineData("do-not-reply@bank.com", "automated sender")]
    [InlineData("notifications+abc@service.io", "automated sender")]
    [InlineData("dana@example.com", null)]
    [InlineData("spam@muted.com", "muted sender")]
    public void Rule_filter_catches_automated_and_muted_senders(string address, string? expected)
    {
        var message = new GraphMessage("id", "c", "s", new GraphAddress("x", address), [], DateTime.UtcNow, "b", null, "focused", false, null, null, false);

        Assert.Equal(expected, MailFilter.Check(message, ["@muted.com"]));
    }

    [Fact]
    public void Header_filter_catches_bulk_mail()
    {
        Assert.Equal("mailing list", MailFilter.CheckHeaders(new Dictionary<string, string> { ["List-Id"] = "x" }));
        Assert.Equal("precedence: bulk", MailFilter.CheckHeaders(new Dictionary<string, string> { ["Precedence"] = "bulk" }));
        Assert.Equal("auto-submitted", MailFilter.CheckHeaders(new Dictionary<string, string> { ["Auto-Submitted"] = "auto-generated" }));
        Assert.Null(MailFilter.CheckHeaders(new Dictionary<string, string> { ["Auto-Submitted"] = "no" }));
    }

    private OutlookMailConnector CreateConnector()
    {
        var scopes = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var graph = new GraphClient(_http, new FakeTokens());
        var triage = new TriageService(_ai, _settings, scopes, TimeProvider.System);
        _engine ??= new ThreadTriageEngine(graph, triage, _settings, scopes, TimeProvider.System, NullLogger<ThreadTriageEngine>.Instance);
        return new OutlookMailConnector(graph, triage, _engine, new FakeTokens(), _settings, scopes, TimeProvider.System, NullLogger<OutlookMailConnector>.Instance);
    }

    private static string Message(string id, string conversation, string subject, string fromName, string fromAddress, string body) => $$"""
        {
          "id": "{{id}}",
          "conversationId": "{{conversation}}",
          "subject": "{{subject}}",
          "from": { "emailAddress": { "name": "{{fromName}}", "address": "{{fromAddress}}" } },
          "toRecipients": [ { "emailAddress": { "name": "Sam Rivera", "address": "sam@example.com" } } ],
          "receivedDateTime": "2026-10-2{{id[^1]}}T15:00:00Z",
          "body": { "contentType": "text", "content": "{{body}}" },
          "webLink": "https://outlook.office.com/mail/{{id}}",
          "inferenceClassification": "focused"
        }
        """;

    internal sealed class FakeTokens(string token = "graph-token") : IMicrosoftTokenProvider
    {
        public bool IsConfigured => true;

        public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) =>
            Task.FromResult(token);
    }

    /// <summary>Answers triage prompts from a script and records what it was asked.</summary>
    internal sealed class ScriptedChatClient : IChatClient, ITriageClientSource
    {
        public Func<string, string> Respond { get; set; } = _ => "{}";

        public List<string> Prompts { get; } = [];

        public List<string> SystemPrompts { get; } = [];

        public ChatFinishReason FinishReason { get; set; } = ChatFinishReason.Stop;

        public ChatOptions? LastOptions { get; private set; }

        public IChatClient? GetTriageClient() => this;

        public string? UnavailableReason(AiSettings? ai = null) => null;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            SystemPrompts.Add(string.Join("\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Text)));
            var prompt = string.Join("\n", list.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
            Prompts.Add(prompt);
            Assert.NotEqual(ReasoningEffort.None, options?.Reasoning?.Effort);
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Respond(prompt))) { FinishReason = FinishReason });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
