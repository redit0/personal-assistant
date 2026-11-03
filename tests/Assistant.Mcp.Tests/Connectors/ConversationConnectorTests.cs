using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Assistant.Agent.Documents;
using Assistant.Agent.Triage;
using Assistant.Connectors.Microsoft;
using Assistant.Connectors.Slack;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Assistant.Core.Text;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Assistant.Mcp.Tests.Connectors.OutlookTriageTests;

namespace Assistant.Mcp.Tests.Connectors;

/// <summary>Teams, Slack, and the document tools. Both chat connectors feed the same triage engine as email.</summary>
public sealed class ConversationConnectorTests : IAsyncLifetime, IDisposable
{
    private const string AskJson = """{"decision":"ask","title":"Review the SOW","summary":"Needs a review.","priority":"normal","confidence":0.9,"reasoning":"Direct request."}""";

    private const string ResolvedJson = """{"decision":"resolved","confidence":0.9,"reasoning":"Answered outside the thread."}""";

    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly FakeHttp _http = new();
    private const string ChatMarker = "Messages in this conversation, oldest first";

    private readonly ScriptedChatClient _ai = new() { Respond = p => p.Contains(ChatMarker) ? Topics(NewTopic("Review the SOW", LastMessage(p))) : AskJson };
    private UserSettingsStore _settings = null!;
    private McpTestHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await McpTestHost.StartAsync();
        _settings = new UserSettingsStore(_settingsFile);
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Teams_reads_active_chats_and_channel_threads_that_involve_you()
    {
        _settings.Update(s =>
        {
            s.Teams.ChatsEnabled = true;
            s.Teams.ChannelsEnabled = true;
        });

        var recent = DateTime.UtcNow.AddHours(-1).ToString("O");
        var old = DateTime.UtcNow.AddDays(-30).ToString("O");
        _http
            .OnUrl("v1.0/me?$select=id,displayName", """{"id":"me1","displayName":"Sam Rivera"}""")
            .OnUrl("me/chats?$expand", $$"""
                {"value":[
                  {"id":"c1","chatType":"oneOnOne","webUrl":"https://teams.microsoft.com/l/chat/c1","lastMessagePreview":{"createdDateTime":"{{recent}}"},
                   "members":[{"displayName":"Sam Rivera"},{"displayName":"Dana Smith"}]},
                  {"id":"c2","chatType":"group","topic":"Old chat","lastMessagePreview":{"createdDateTime":"{{old}}"},"members":[]}
                ]}
                """)
            .OnUrl("chats/c1/messages", $$"""
                {"value":[
                  {"id":"m2","messageType":"unknownFutureValue","createdDateTime":"{{recent}}","body":{"content":"<systemEventMessage/>"} },
                  {{TeamsMessage("m1", "Dana Smith", "u2", "tenant-1", "<p>Can you review the <b>SOW</b> by Friday?</p>", recent)}}
                ]}
                """)
            .OnUrl("me/joinedTeams", """{"value":[{"id":"T1","displayName":"Delivery"}]}""")
            .OnUrl("teams/T1/channels?$select", """{"value":[{"id":"ch1","displayName":"General"}]}""")
            .OnUrl("teams/T1/channels/ch1/messages?$top=50&$expand=replies", $$"""
                {"value":[
                  {{TeamsMessage("r1", "Ann Guest", "u3", "tenant-2", "<p><at id=\"0\">Sam Rivera</at> can you look at the estimate?</p>", recent, "me1",
                      $$"""[{{TeamsMessage("r1a", "Bob Lee", "u4", "tenant-1", "<p>+1</p>", recent)}}]""")}},
                  {{TeamsMessage("r2", "Bob Lee", "u4", "tenant-1", "<p>Lunch anyone?</p>", recent)}}
                ]}
                """);

        var connector = CreateTeams();
        var first = await connector.FetchAsync(default);

        Assert.Equal(2, _ai.Prompts.Count);
        var chatPrompt = Assert.Single(_ai.Prompts, p => p.Contains("Can you review the SOW by Friday?"));
        Assert.Contains("<untrusted_content source=\"Teams chat\">", chatPrompt);
        Assert.Contains("This is a Teams chat conversation.", chatPrompt);
        var channelPrompt = Assert.Single(_ai.Prompts, p => p.Contains("@Sam Rivera can you look at the estimate?"));
        Assert.Contains("[This message @mentions the user.]", channelPrompt);
        Assert.Contains("Delivery › General", channelPrompt);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(SenderTrust.Organization, first.Items.Single(i => i.ExternalId!.StartsWith("teams:c1|")).Trust);
        Assert.Equal(SenderTrust.External, first.Items.Single(i => i.ExternalId == "teams:ch1:r1").Trust);

        var signals = await _host.WithDbAsync(db => db.Signals.Where(s => s.Source == "teams").Select(s => s.ExternalId).ToListAsync());
        Assert.DoesNotContain("c1:m2", signals); // System event.
        Assert.DoesNotContain(signals, s => s.Contains("r2")); // Thread that doesn't involve you.

        // Nothing new: the chat isn't opened again and nothing is re-triaged.
        await connector.FetchAsync(default);
        Assert.Equal(2, _ai.Prompts.Count);
        Assert.Single(_http.Requests, r => r.Request.RequestUri!.ToString().Contains("chats/c1/messages"));
    }

    [Fact]
    public async Task Slack_reads_direct_messages_and_mentions()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s => s.Slack.Enabled = true);

        var ts = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/users.conversations", """{"ok":true,"channels":[{"id":"D1","is_im":true,"user":"U2"}],"response_metadata":{"next_cursor":""}}""")
            .OnUrl("api/conversations.history", $$"""
                {"ok":true,"messages":[
                  {"type":"message","subtype":"channel_join","user":"U2","text":"joined","ts":"{{ts}}"},
                  {"type":"message","user":"U2","team":"T1","text":"Can you send <@U1> the deck? <https://x.com/deck|link>","ts":"{{ts}}"}
                ]}
                """)
            .OnUrl("api/search.messages", $$"""
                {"ok":true,"messages":{"matches":[
                  {"channel":{"id":"C9","name":"proj"},"ts":"{{ts}}","permalink":"https://example.slack.com/archives/C9/p1?thread_ts={{ts}}&cid=C9","text":"x"}
                ]} }
                """)
            .OnUrl("api/conversations.replies", $$"""
                {"ok":true,"messages":[
                  {"type":"message","user":"U3","user_team":"T9","text":"<@U1> what's the status of the integration?","ts":"{{ts}}","thread_ts":"{{ts}}"}
                ]}
                """)
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("users.info") && Body(r).Contains("user=U1"), """{"ok":true,"user":{"id":"U1","team_id":"T1","profile":{"display_name":"Sam"}}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("users.info") && Body(r).Contains("user=U2"), """{"ok":true,"user":{"id":"U2","team_id":"T1","profile":{"display_name":"Dana"}}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("users.info") && Body(r).Contains("user=U3"), """{"ok":true,"user":{"id":"U3","team_id":"T9","profile":{"real_name":"Pat Client"}}}""");

        var connector = CreateSlack();
        var first = await connector.FetchAsync(default);

        Assert.Equal(2, _ai.Prompts.Count);
        var dm = Assert.Single(_ai.Prompts, p => p.Contains("Can you send @Sam the deck? link (https://x.com/deck)"));
        Assert.Contains("Slack DM with Dana", dm);
        var mention = Assert.Single(_ai.Prompts, p => p.Contains("@Sam what's the status of the integration?"));
        Assert.Contains("#proj", mention);

        Assert.Equal(SenderTrust.Organization, first.Items.Single(i => i.ExternalId!.StartsWith("slack:D1|")).Trust);
        Assert.Equal(SenderTrust.External, first.Items.Single(i => i.ExternalId == $"slack:C9:{ts}").Trust);

        var dmSignal = await _host.WithDbAsync(db => db.Signals.SingleAsync(s => s.Source == "slack" && s.ThreadKey == "D1"));
        Assert.Equal($"https://example.slack.com/archives/D1/p{ts.Replace(".", "")}", dmSignal.Url);

        // Nothing new: no extra triage.
        await connector.FetchAsync(default);
        Assert.Equal(2, _ai.Prompts.Count);
    }

    [Fact]
    public async Task Slack_resolves_a_mention_answered_in_the_channel_instead_of_the_thread()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeDirectMessages = false;
        });
        _ai.Respond = p => p.Contains("Later messages the user sent outside this thread") ? ResolvedJson : AskJson;

        var asked = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        var answered = SlackClient.ToTs(DateTime.UtcNow.AddMinutes(-30));
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("search.messages"), $$"""
                {"ok":true,"messages":{"matches":[
                  {"channel":{"id":"C9","name":"proj"},"user":"U3","ts":"{{asked}}","permalink":"https://example.slack.com/archives/C9/p1?thread_ts={{asked}}&cid=C9","text":"<@U1> is the integration live?"}
                ]} }
                """)
            .OnUrl("api/conversations.replies", $$"""
                {"ok":true,"messages":[
                  {"type":"message","user":"U3","user_team":"T1","text":"<@U1> is the integration live?","ts":"{{asked}}","thread_ts":"{{asked}}"}
                ]}
                """)
            .OnUrl("api/users.info", """{"ok":true,"user":{"id":"U3","team_id":"T1","profile":{"display_name":"Pat"}}}""");

        var connector = CreateSlack();
        var first = await connector.FetchAsync(default);
        Assert.Single(_ai.Prompts);
        Assert.Contains(first.Items, i => i.ExternalId == $"slack:C9:{asked}");

        // You answered in the channel itself, not in Pat's thread.
        _http.On(r => r.RequestUri!.AbsolutePath.EndsWith("search.messages") && Body(r).Contains("from"), $$"""
            {"ok":true,"messages":{"matches":[
              {"channel":{"id":"C9","name":"proj"},"user":"U1","ts":"{{answered}}","permalink":"https://example.slack.com/archives/C9/p2","text":"Pat, the integration went live this morning."}
            ]} }
            """);

        var second = await connector.FetchAsync(default);

        Assert.Equal(2, _ai.Prompts.Count);
        Assert.Contains("Pat, the integration went live this morning.", _ai.Prompts[1]);
        Assert.Empty(second.Items);

        // The answer is kept only as context; it isn't triaged as a thread of its own.
        var context = await _host.WithDbAsync(db => db.Signals.SingleAsync(s => s.Source == "slack" && s.ExternalId == $"C9:{answered}"));
        Assert.NotNull(context.FilteredReason);
        await connector.FetchAsync(default);
        Assert.Equal(2, _ai.Prompts.Count);
    }

    [Fact]
    public async Task Asks_from_a_sender_you_keep_dismissing_go_to_needs_a_look()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeDirectMessages = false;
        });
        await _host.WithDbAsync(async db =>
        {
            for (var i = 0; i < 3; i++)
            {
                db.Items.Add(new Item
                {
                    Title = $"Old ask {i}", Source = "slack", Requester = "Pat", Status = ItemStatus.Dismissed,
                    CreatedAt = DateTime.UtcNow.AddDays(-5), UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow.AddDays(-4),
                });
            }

            db.Items.Add(new Item { Title = "Kept", Source = "slack", Requester = "Dana", Status = ItemStatus.Done, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, CompletedAt = DateTime.UtcNow });
            db.ItemFeedback.Add(new Core.Signals.ItemFeedback { ItemId = 999, Useful = true, ItemTitle = "Earlier", Requester = "Dana", Source = "slack", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            return 0;
        });
        _ai.Respond = _ => """{"decision":"ask","title":"Answer Pat","requester":"Pat","confidence":0.9,"reasoning":"Direct question."}""";

        var asked = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/search.messages", $$"""
                {"ok":true,"messages":{"matches":[
                  {"channel":{"id":"C9","name":"proj"},"user":"U3","ts":"{{asked}}","permalink":"https://example.slack.com/archives/C9/p1","text":"<@U1> quick question"}
                ]} }
                """)
            .OnUrl("api/conversations.replies", $$"""{"ok":true,"messages":[{"type":"message","user":"U3","text":"<@U1> quick question","ts":"{{asked}}"}]}""")
            .OnUrl("api/users.info", """{"ok":true,"user":{"id":"U3","team_id":"T1","profile":{"display_name":"Pat"}}}""");

        var snapshot = await CreateSlack().FetchAsync(default);

        Assert.Contains("- Pat: 0 useful, 3 not useful", _ai.SystemPrompts[0]);
        Assert.Contains("- Dana: 2 useful, 0 not useful", _ai.SystemPrompts[0]);
        var item = Assert.Single(snapshot.Items);
        Assert.True(item.NeedsReview);
        Assert.Contains("dismissed 3 earlier items from Pat", item.Reasoning);
    }

    [Fact]
    public async Task Slack_keeps_a_mention_in_its_thread_when_the_channel_is_read_later()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeDirectMessages = false;
        });

        var asked = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        var message = $$"""{"type":"message","user":"U3","user_team":"T1","text":"<@U1> is the integration live?","ts":"{{asked}}"}""";
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/search.messages", $$"""
                {"ok":true,"messages":{"matches":[
                  {"channel":{"id":"C9","name":"proj"},"user":"U3","ts":"{{asked}}","permalink":"https://example.slack.com/archives/C9/p1","text":"<@U1> is the integration live?"}
                ]} }
                """)
            .OnUrl("api/conversations.replies", $$"""{"ok":true,"messages":[{{message}}]}""")
            .OnUrl("api/conversations.history", $$"""{"ok":true,"messages":[{{message}}]}""")
            .OnUrl("api/users.info", """{"ok":true,"user":{"id":"U3","team_id":"T1","profile":{"display_name":"Pat"}}}""");

        var connector = CreateSlack();
        await connector.FetchAsync(default);

        // Now you start reading #proj in full.
        _settings.Update(s => s.Slack.Channels = [new SlackChannelChoice { Id = "C9", Name = "proj" }]);
        var second = await connector.FetchAsync(default);

        var signal = await _host.WithDbAsync(db => db.Signals.SingleAsync(s => s.Source == "slack" && s.ExternalId == $"C9:{asked}"));
        Assert.Equal($"C9:{asked}", signal.ThreadKey);
        Assert.Single(second.Items, i => i.ExternalId == $"slack:C9:{asked}");
        Assert.Single(_ai.Prompts);
    }

    [Fact]
    public async Task Slack_puts_moved_mention_messages_back_in_their_thread()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeDirectMessages = false;
            s.Slack.IncludeMentions = false;
        });

        var asked = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        await _host.WithDbAsync(async db =>
        {
            db.Signals.Add(new Core.Signals.Signal { Source = "slack", ExternalId = $"C9:{asked}", ThreadKey = "C9", Kind = "channel", Body = "x", ReceivedAt = SlackClient.FromTs(asked) });
            db.TriageDecisions.Add(new Core.Signals.TriageDecision { Source = "slack", Key = $"C9:{asked}", Outcome = Core.Signals.TriageOutcome.Ask, Title = "Answer Pat", LastSeenAt = SlackClient.FromTs(asked) });
            await db.SaveChangesAsync();
            return 0;
        });
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/search.messages", """{"ok":true,"messages":{"matches":[]}}""");

        await CreateSlack().FetchAsync(default);

        var signal = await _host.WithDbAsync(db => db.Signals.SingleAsync(s => s.ExternalId == $"C9:{asked}"));
        Assert.Equal($"C9:{asked}", signal.ThreadKey);
    }

    [Fact]
    public async Task Teams_resolves_a_channel_ask_answered_in_a_direct_chat()
    {
        _settings.Update(s =>
        {
            s.Teams.ChatsEnabled = true;
            s.Teams.ChannelsEnabled = true;
        });
        _ai.Respond = p => p.Contains("Later messages the user sent outside this thread") ? ResolvedJson
            : p.Contains("estimate is in your inbox") ? """{"decision":"fyi","confidence":0.9}"""
            : AskJson;

        var asked = DateTime.UtcNow.AddHours(-2).ToString("O");
        var hello = DateTime.UtcNow.AddDays(-3).ToString("O");
        var answered = DateTime.UtcNow.AddMinutes(-10).ToString("O");
        _http
            .OnUrl("v1.0/me?$select=id,displayName", """{"id":"me1","displayName":"Sam Rivera"}""")
            .OnUrl("me/chats?$expand", $$"""
                {"value":[
                  {"id":"c3","chatType":"oneOnOne","lastMessagePreview":{"createdDateTime":"{{answered}}"},
                   "members":[{"displayName":"Sam Rivera"},{"displayName":"Ann Guest"}]}
                ]}
                """)
            .OnUrl("chats/c3/messages", $$"""
                {"value":[
                  {{TeamsMessage("d1", "Ann Guest", "u3", "tenant-1", "<p>Thanks for the call</p>", hello)}},
                  {{TeamsMessage("d2", "Sam Rivera", "me1", "tenant-1", "<p>Ann, the estimate is in your inbox.</p>", answered)}}
                ]}
                """)
            .OnUrl("me/joinedTeams", """{"value":[{"id":"T1","displayName":"Delivery"}]}""")
            .OnUrl("teams/T1/channels?$select", """{"value":[{"id":"ch1","displayName":"General"}]}""")
            .OnUrl("teams/T1/channels/ch1/messages?$top=50&$expand=replies", $$"""
                {"value":[
                  {{TeamsMessage("r1", "Ann Guest", "u3", "tenant-1", "<p><at id=\"0\">Sam Rivera</at> can you send me the estimate?</p>", asked, "me1")}}
                ]}
                """);

        var connector = CreateTeams();
        var first = await connector.FetchAsync(default);
        Assert.Contains(first.Items, i => i.ExternalId == "teams:ch1:r1");

        // The DM came after the ask, so the next sync takes another look at the channel item.
        var second = await connector.FetchAsync(default);

        var recheck = Assert.Single(_ai.Prompts, p => p.Contains("Later messages the user sent outside this thread"));
        Assert.Contains("Ann, the estimate is in your inbox.", recheck);
        Assert.DoesNotContain(second.Items, i => i.ExternalId == "teams:ch1:r1");

        var count = _ai.Prompts.Count;
        await connector.FetchAsync(default);
        Assert.Equal(count, _ai.Prompts.Count);
    }

    [Theory]
    [InlineData("19:abc@thread.v2", "19:abc@thread.v2")]
    [InlineData("19:def@thread.tacv2:1712345678901", "19:def@thread.tacv2")]
    [InlineData("ch1:r1", "ch1")]
    public void Teams_place_of_a_thread_is_its_chat_or_channel(string key, string place) =>
        Assert.Equal(place, TeamsConnector.PlaceOf(key));

    [Fact]
    public async Task Slack_stores_a_reply_sent_to_the_channel_once()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeMentions = false;
        });

        var root = SlackClient.ToTs(DateTime.UtcNow.AddHours(-2));
        var reply = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/users.conversations", """{"ok":true,"channels":[{"id":"D1","is_im":true,"user":"U2"}],"response_metadata":{"next_cursor":""}}""")
            .OnUrl("api/conversations.history", $$"""
                {"ok":true,"messages":[
                  {"type":"message","subtype":"thread_broadcast","user":"U2","text":"Also posting here: please review","ts":"{{reply}}","thread_ts":"{{root}}"},
                  {"type":"message","user":"U2","text":"Starting a thread","ts":"{{root}}","thread_ts":"{{root}}","reply_count":1,"latest_reply":"{{reply}}"}
                ]}
                """)
            .OnUrl("api/conversations.replies", $$"""
                {"ok":true,"messages":[
                  {"type":"message","user":"U2","text":"Starting a thread","ts":"{{root}}","thread_ts":"{{root}}","reply_count":1},
                  {"type":"message","subtype":"thread_broadcast","user":"U2","text":"Also posting here: please review","ts":"{{reply}}","thread_ts":"{{root}}"}
                ]}
                """)
            .OnUrl("api/users.info", """{"ok":true,"user":{"id":"U2","team_id":"T1","profile":{"display_name":"Dana"}}}""");

        _http.OnUrl("api/search.messages", """{"ok":true,"messages":{"matches":[]}}""");

        var snapshot = await CreateSlack().FetchAsync(default);

        Assert.Empty(snapshot.Warnings);
        var signals = await _host.WithDbAsync(db => db.Signals.Where(s => s.Source == "slack").ToListAsync());
        Assert.Equal(2, signals.Count);
        Assert.All(signals, s => Assert.Equal($"D1:{root}", s.ThreadKey));
    }

    [Fact]
    public async Task Slack_skips_a_channel_it_cant_read_and_keeps_going()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeMentions = false;
            s.Slack.Channels = [new SlackChannelChoice { Id = "C404", Name = "old-project" }];
        });

        var ts = SlackClient.ToTs(DateTime.UtcNow.AddHours(-1));
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/users.conversations", """{"ok":true,"channels":[{"id":"D1","is_im":true,"user":"U2"}],"response_metadata":{"next_cursor":""}}""")
            .OnUrl("api/search.messages", """{"ok":true,"messages":{"matches":[]}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("conversations.history") && Body(r).Contains("channel=C404"), """{"ok":false,"error":"channel_not_found"}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("conversations.history") && Body(r).Contains("channel=D1"), $$"""
                {"ok":true,"messages":[{"type":"message","user":"U2","text":"Can you check the build?","ts":"{{ts}}"}]}
                """)
            .OnUrl("api/users.info", """{"ok":true,"user":{"id":"U2","team_id":"T1","profile":{"display_name":"Dana"}}}""");

        var snapshot = await CreateSlack().FetchAsync(default);

        var warning = Assert.Single(snapshot.Warnings);
        Assert.Contains("#old-project", warning);
        Assert.Contains("channel_not_found", warning);
        Assert.Single(snapshot.Items, i => i.ExternalId!.StartsWith("slack:D1|"));
    }

    [Fact]
    public async Task A_dm_holds_separate_items_for_unrelated_requests()
    {
        var connector = SlackDm();
        _ai.Respond = _ => Topics(NewTopic("Fix the chart", 1));
        var first = await SyncDmAsync(connector, ("U2", "The chart axis is wrong", -60));

        // A later, unrelated request gets its own item, and the first one is left as it was.
        _ai.Respond = _ => Topics(NewTopic("Write the discovery questions", 2));
        var second = await SyncDmAsync(connector, ("U2", "Can you write questions for Friday?", -30));

        Assert.Single(first.Items);
        Assert.Equal(2, second.Items.Count);
        var chart = Assert.Single(second.Items, i => i.Title == "Fix the chart");
        var questions = Assert.Single(second.Items, i => i.Title == "Write the discovery questions");
        Assert.NotEqual(chart.ExternalId, questions.ExternalId);
        Assert.Contains("The chart axis is wrong", chart.SourceContent);
        Assert.Contains("questions for Friday", questions.SourceContent);
    }

    [Fact]
    public async Task One_message_with_two_requests_becomes_two_items()
    {
        _ai.Respond = _ => Topics(NewTopic("Write the questions", 1), NewTopic("Say how the assistant was built", 1));

        var snapshot = await SyncDmAsync(SlackDm(), ("U2", "Can you write questions, and did you build it in Code?", -30));

        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal(2, snapshot.Items.Select(i => i.ExternalId).Distinct().Count());
    }

    [Fact]
    public async Task Agreeing_keeps_the_topic_open_and_pointing_at_the_request()
    {
        var connector = SlackDm();
        _ai.Respond = _ => Topics(NewTopic("Write the questions", 1));
        var first = await SyncDmAsync(connector, ("U2", "Can you write questions for Friday?", -60));

        _ai.Respond = _ => """{"topics":[{"topic":1,"messages":[2],"judgment":{"decision":"commitment","title":"Write the questions","confidence":0.9}}]}""";
        var second = await SyncDmAsync(connector, ("U1", "Sure", -30));

        Assert.Contains("[1] ask: \"Write the questions\"", _ai.Prompts[^1]);
        var item = Assert.Single(second.Items);
        Assert.Equal(first.Items[0].ExternalId, item.ExternalId);
        Assert.Equal(ItemType.Commitment, item.Type);
        Assert.Contains("questions for Friday", item.SourceContent);
    }

    [Fact]
    public async Task A_request_shown_only_as_context_cant_start_a_topic()
    {
        var connector = SlackDm();
        _ai.Respond = _ => Topics(NewTopic("Fix the chart", 1));
        await SyncDmAsync(connector, ("U2", "The chart axis is wrong", -60));
        await _host.WithDbAsync(async db =>
        {
            foreach (var d in db.TriageDecisions.Where(d => d.Source == "slack" && d.Key.Contains("|")))
            {
                d.Outcome = Core.Signals.TriageOutcome.Resolved;
            }

            return await db.SaveChangesAsync();
        });

        // Message 1 was read earlier, so citing it can't bring the request back.
        var second = await SyncDmAsync(connector, ("U2", "Thanks", -30));

        Assert.Empty(second.Items);
    }

    [Fact]
    public async Task A_topic_whose_item_you_closed_isnt_offered_again()
    {
        var connector = SlackDm();
        _ai.Respond = _ => Topics(NewTopic("Fix the chart", 1));
        var first = await SyncDmAsync(connector, ("U2", "The chart axis is wrong", -60));
        await _host.WithDbAsync(async db =>
        {
            db.Items.Add(new Item
            {
                Title = "Fix the chart", Source = "slack", ExternalId = first.Items[0].ExternalId, Status = ItemStatus.Done,
                CompletedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        _ai.Respond = _ => Topics(NewTopic("Write the questions", 2));
        var second = await SyncDmAsync(connector, ("U2", "Can you write questions for Friday?", -30));

        Assert.Contains("Open topics in this conversation:\n(none)", _ai.Prompts[^1].ReplaceLineEndings("\n"));
        Assert.Equal("Write the questions", Assert.Single(second.Items).Title);
    }

    [Fact]
    public async Task An_open_whole_dm_judgment_moves_to_a_topic_with_its_item()
    {
        var at = DateTime.UtcNow.AddHours(-2);
        await _host.WithDbAsync(async db =>
        {
            db.TriageDecisions.Add(new Core.Signals.TriageDecision { Source = "slack", Key = "D1", Outcome = Core.Signals.TriageOutcome.Ask, Title = "Send the deck", LastSeenAt = at, TriagedAt = at });
            db.Items.Add(new Item { Title = "Send the deck", Source = "slack", ExternalId = "slack:D1", CreatedAt = at, UpdatedAt = at });
            return await db.SaveChangesAsync();
        });
        _ai.Respond = _ => Topics(NewTopic("Write the questions", 1));

        var snapshot = await SyncDmAsync(SlackDm(), ("U2", "Can you write questions for Friday?", -30));

        Assert.Equal(2, snapshot.Items.Count);
        var kept = Assert.Single(snapshot.Items, i => i.Title == "Send the deck");
        var item = await _host.WithDbAsync(db => db.Items.SingleAsync(i => i.Title == "Send the deck"));
        Assert.Equal(kept.ExternalId, item.ExternalId);
        Assert.StartsWith("slack:D1|", item.ExternalId);
    }

    [Theory]
    [InlineData("D1", "D1")]
    [InlineData("D1|638000", "D1")]
    [InlineData("19:abc@thread.v2|638000", "19:abc@thread.v2")]
    [InlineData("C9:1793.0001", "C9:1793.0001")]
    public void A_topic_key_leads_back_to_its_conversation(string key, string conversation) =>
        Assert.Equal(conversation, Core.Signals.ConversationTopic.ConversationOf(key));

    private SlackConnector SlackDm()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _settings.Update(s =>
        {
            s.Slack.Enabled = true;
            s.Slack.IncludeMentions = false;
        });
        _http
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","team":"Acme","url":"https://example.slack.com/"}""")
            .OnUrl("api/users.conversations", """{"ok":true,"channels":[{"id":"D1","is_im":true,"user":"U2"}],"response_metadata":{"next_cursor":""}}""")
            .OnUrl("api/search.messages", """{"ok":true,"messages":{"matches":[]}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("users.info") && Body(r).Contains("user=U1"), """{"ok":true,"user":{"id":"U1","team_id":"T1","profile":{"display_name":"Sam"}}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("users.info") && Body(r).Contains("user=U2"), """{"ok":true,"user":{"id":"U2","team_id":"T1","profile":{"display_name":"Dana"}}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("conversations.history"), () => _dmHistory);
        return CreateSlack();
    }

    /// <summary>Adds a message to the DM and runs a sync. Minutes are relative to now.</summary>
    private async Task<Core.Connectors.ConnectorSnapshot> SyncDmAsync(SlackConnector connector, (string User, string Text, int Minutes) message)
    {
        var ts = SlackClient.ToTs(DateTime.UtcNow.AddMinutes(message.Minutes));
        _dmHistory = JsonSerializer.Serialize(new
        {
            ok = true,
            messages = new[] { new { type = "message", user = message.User, team = "T1", text = message.Text, ts } },
        });
        return await connector.FetchAsync(default);
    }

    private string _dmHistory = """{"ok":true,"messages":[]}""";

    private static string Topics(params string[] topics) => $$"""{"topics":[{{string.Join(",", topics)}}]}""";

    private static string NewTopic(string title, int message) =>
        $$$"""{"topic":null,"messages":[{{{message}}}],"judgment":{"decision":"ask","title":"{{{title}}}","summary":"Needs doing.","priority":"normal","confidence":0.9,"reasoning":"Direct request."}}""";

    /// <summary>The number of the last message in a chat prompt.</summary>
    private static int LastMessage(string prompt) =>
        prompt.Split('\n').Count(line => line.StartsWith("Message ", StringComparison.Ordinal));

    [Fact]
    public async Task Slack_explains_a_missing_scope()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        _http.OnUrl("api/auth.test", """{"ok":false,"error":"missing_scope","needed":"search:read"}""");
        var client = new SlackClient(_http, _host.Services.GetRequiredService<ISecretStore>());

        var ex = await Assert.ThrowsAsync<Core.Connectors.ConnectorException>(() => client.AuthTestAsync(default));
        Assert.Contains("search:read", ex.Message);
    }

    [Theory]
    [InlineData("Hi <@U1>, see <#C2|general> and <https://a.com|the doc> &amp; <https://b.com>", "Hi @Sam, see #general and the doc (https://a.com) & https://b.com")]
    [InlineData("<!here> standup moved", "@here standup moved")]
    [InlineData("<@U9> unknown", "@U9 unknown")]
    public void Slack_markup_becomes_plain_text(string markup, string expected) =>
        Assert.Equal(expected, SlackText.ToText(markup, id => id == "U1" ? "Sam" : null));

    [Fact]
    public void Teams_html_becomes_plain_text() =>
        Assert.Equal("@Dana Smith please check\nthe budget & dates", HtmlText.ToText("<p><at id=\"0\">Dana Smith</at> please check</p><p>the budget &amp; dates</p>"));

    [Fact]
    public void Tenant_is_read_from_the_token()
    {
        Assert.Equal("tenant-1", GraphClient.TenantOf(Jwt("tenant-1")));
        Assert.Null(GraphClient.TenantOf("not-a-jwt"));
    }

    [Theory]
    [InlineData("no-reply@sharepointonline.com", "Dana Smith mentioned you in Budget.docx", true)]
    [InlineData("notifications@contoso.sharepoint.com", "Dana shared \"Plan.pptx\" with you", true)]
    [InlineData("no-reply@sharepointonline.com", "Your weekly digest", false)]
    [InlineData("dana@example.com", "Dana mentioned you in Budget.docx", false)]
    public void Document_notifications_are_recognized(string from, string subject, bool expected)
    {
        var message = new GraphMessage("id", "c", subject, new GraphAddress("SharePoint", from), [], DateTime.UtcNow, "b", null, "focused", false, null, null, false);
        Assert.Equal(expected, MailFilter.IsDocumentNotification(message));
    }

    [Fact]
    public void Office_files_yield_their_text()
    {
        var docx = Zip(("word/document.xml",
            """<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Scope of </w:t></w:r><w:r><w:t>work</w:t></w:r></w:p><w:p><w:r><w:t>Phase 1</w:t></w:r></w:p></w:body></w:document>"""));
        Assert.Equal("Scope of work\nPhase 1", GraphClient.ExtractText(docx, ".docx")!.ReplaceLineEndings("\n"));

        var pptx = Zip(
            ("ppt/slides/slide2.xml", """<p:sld xmlns:p="p" xmlns:a="a"><a:p><a:r><a:t>Second</a:t></a:r></a:p></p:sld>"""),
            ("ppt/slides/slide1.xml", """<p:sld xmlns:p="p" xmlns:a="a"><a:p><a:r><a:t>First</a:t></a:r></a:p></p:sld>"""),
            ("ppt/slides/slide10.xml", """<p:sld xmlns:p="p" xmlns:a="a"><a:p><a:r><a:t>Tenth</a:t></a:r></a:p></p:sld>"""));
        Assert.Equal(["First", "Second", "Tenth"], GraphClient.ExtractText(pptx, ".pptx")!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        Assert.Null(GraphClient.ExtractText(Encoding.UTF8.GetBytes("not a zip"), ".docx"));
    }

    [Fact]
    public void Work_iq_answer_is_the_last_message()
    {
        using var doc = JsonDocument.Parse("""
            {"messages":[
              {"text":"What did we decide?"},
              {"text":"You agreed to send the SOW by Friday.","attributions":[
                {"attributionType":"citation","providerDisplayName":"Weekly sync","seeMoreWebUrl":"https://teams.microsoft.com/meet/1"},
                {"attributionType":"citation","providerDisplayName":"Weekly sync","seeMoreWebUrl":"https://teams.microsoft.com/meet/1"}]}
            ]}
            """);

        var answer = WorkIqClient.Parse("conv1", doc.RootElement);

        Assert.Equal("You agreed to send the SOW by Friday.", answer.Text);
        Assert.Equal("Weekly sync", Assert.Single(answer.Sources).Title);
    }

    [Fact]
    public async Task Document_tools_stay_off_until_turned_on()
    {
        var tools = new DocumentTools(new GraphClient(_http, new FakeTokens()), new WorkIqClient(_http, new FakeTokens()), _settings);

        var docs = await Assert.ThrowsAsync<ToolException>(() => tools.SearchDocumentsAsync("budget"));
        Assert.Contains("turned off", docs.Message);
        var workIq = await Assert.ThrowsAsync<ToolException>(() => tools.AskWorkIqAsync("What's due?"));
        Assert.Contains("turned off", workIq.Message);

        _settings.Update(s => s.SharePoint.DocumentsEnabled = true);
        await Assert.ThrowsAsync<ToolException>(() => tools.ReadDocumentAsync("https://evil.example.com/doc.docx"));
    }

    [Fact]
    public async Task Document_search_wraps_snippets_as_untrusted()
    {
        _settings.Update(s => s.SharePoint.DocumentsEnabled = true);
        _http.OnUrl("v1.0/search/query", """
            {"value":[{"hitsContainers":[{"hits":[{"summary":"the <c0>budget</c0> for Q4","resource":{"id":"i1","name":"Budget.xlsx","webUrl":"https://contoso.sharepoint.com/b.xlsx",
              "lastModifiedDateTime":"2026-10-01T00:00:00Z","lastModifiedBy":{"user":{"displayName":"Dana Smith"}},"parentReference":{"driveId":"d1"}}}]}]}]}
            """);
        var tools = new DocumentTools(new GraphClient(_http, new FakeTokens()), new WorkIqClient(_http, new FakeTokens()), _settings);

        var hit = Assert.Single(await tools.SearchDocumentsAsync("budget"));

        Assert.Equal("Budget.xlsx", hit.Name);
        Assert.Equal("Dana Smith", hit.LastModifiedBy);
        Assert.Contains("<untrusted_content source=\"sharepoint\">\nthe budget for Q4", hit.Snippet);
    }

    private TeamsConnector CreateTeams()
    {
        var scopes = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var tokens = new FakeTokens(Jwt("tenant-1"));
        var graph = new GraphClient(_http, tokens);
        var triage = new TriageService(_ai, _settings, scopes, TimeProvider.System);
        var engine = new ThreadTriageEngine(graph, triage, _settings, scopes, TimeProvider.System, NullLogger<ThreadTriageEngine>.Instance);
        return new TeamsConnector(graph, triage, engine, tokens, _settings, scopes, TimeProvider.System, NullLogger<TeamsConnector>.Instance) { Pace = TimeSpan.Zero };
    }

    private SlackConnector CreateSlack()
    {
        var scopes = _host.Services.GetRequiredService<IServiceScopeFactory>();
        var graph = new GraphClient(_http, new FakeTokens());
        var triage = new TriageService(_ai, _settings, scopes, TimeProvider.System);
        var engine = new ThreadTriageEngine(graph, triage, _settings, scopes, TimeProvider.System, NullLogger<ThreadTriageEngine>.Instance);
        var slack = new SlackClient(_http, _host.Services.GetRequiredService<ISecretStore>());
        return new SlackConnector(slack, triage, engine, _settings, scopes, TimeProvider.System, NullLogger<SlackConnector>.Instance) { Pace = TimeSpan.Zero };
    }

    private static string TeamsMessage(string id, string fromName, string fromId, string tenant, string html, string at, string? mentions = null, string? replies = null) => $$"""
        {
          "id": "{{id}}",
          "messageType": "message",
          "createdDateTime": "{{at}}",
          "lastModifiedDateTime": "{{at}}",
          "from": { "user": { "id": "{{fromId}}", "displayName": "{{fromName}}", "tenantId": "{{tenant}}" } },
          "body": { "contentType": "html", "content": {{JsonSerializer.Serialize(html)}} },
          "webUrl": "https://teams.microsoft.com/l/message/{{id}}",
          "mentions": [{{(mentions is null ? "" : "{\"id\":0,\"mentionText\":\"Sam Rivera\",\"mentioned\":{\"user\":{\"id\":\"" + mentions + "\"}}}")}}]
          {{(replies is null ? "" : $",\"replies\": {replies}")}}
        }
        """;

    private static string Jwt(string tenant)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{Part("""{"alg":"none"}""")}.{Part($$"""{"tid":"{{tenant}}"}""")}.sig";
    }

    private static string Body(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }
}
