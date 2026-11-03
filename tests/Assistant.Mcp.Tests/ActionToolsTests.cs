using System.Text.Json;
using Assistant.Actions;
using Assistant.Connectors.Atlassian;
using Assistant.Connectors.GitHub;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Items;
using Assistant.Core.Repos;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Core.Tools;
using Assistant.Mcp.Tests.Connectors;
using Assistant.Tools.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assistant.Mcp.Tests;

/// <summary>The source-action tools, end to end through the MCP server, against fake host APIs.</summary>
public sealed class ActionToolsTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly FakeHttp _http = new();
    private McpTestHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var settings = new UserSettingsStore(_settingsFile);
        settings.Update(s =>
        {
            s.GitHub.Enabled = true;
            s.Atlassian.Email = "sam@example.com";
            s.AzureDevOps.Organizations["contoso"] = true;
        });

        _host = await McpTestHost.StartAsync(services =>
        {
            services.AddSingleton(settings);
            services.AddSingleton<IHttpClientFactory>(_http);
            services.AddSingleton<IGitHubCliToken, NoCli>();
            services.AddSingleton<IMicrosoftTokenProvider, FakeTokens>();
            services.AddSingleton<IAzureCliToken, NoAzureCli>();
            services.AddSingleton<GitHubConnector>();
            services.AddSingleton<AzureDevOpsConnector>();
            services.AddSingleton<GraphClient>();
            services.AddSingleton<Assistant.Connectors.Slack.SlackClient>();
            services.AddAssistantActions();
        });

        var secrets = _host.Services.GetRequiredService<ISecretStore>();
        secrets.Set(SecretNames.GitHubToken, "ghp_test");
        secrets.Set(SecretNames.JiraApiToken, "jira");
        secrets.Set(SecretNames.BitbucketApiToken, "bb");

        _host.Approvals.Answer = true;
        _client = await _host.ConnectAsync("claude-code");
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Action_tools_are_listed_as_open_world_and_ask_by_default()
    {
        var tools = (await _client.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var name in new[] { "create_pull_request", "comment", "review_pull_request", "change_status", "draft_email_reply" })
        {
            Assert.True(tools[name].ProtocolTool.Annotations?.OpenWorldHint, name);
            Assert.False(tools[name].ProtocolTool.Annotations?.ReadOnlyHint, name);
        }

        var catalog = _host.Services.GetRequiredService<ToolCatalog>();
        Assert.Equal(ToolPolicy.Ask, catalog.Find("create_pull_request")!.DefaultPolicy);
    }

    [Fact]
    public async Task Opens_a_github_pull_request_after_approval_and_links_it_to_the_item()
    {
        var itemId = await AddRepoAndItemAsync("web", "git@github.com:acme/web.git", SenderTrust.Organization);
        _http
            .OnUrl("repos/acme/web", """{"default_branch":"main"}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/repos/acme/web/pulls",
                """{"number":14,"html_url":"https://github.com/acme/web/pull/14"}""");

        var result = await CallAsync("create_pull_request", new()
        {
            ["repo"] = "web", ["sourceBranch"] = "assistant/1-fix", ["title"] = "Fix login", ["itemId"] = itemId,
        });

        Assert.Equal("https://github.com/acme/web/pull/14", result.GetProperty("url").GetString());
        var request = Assert.Single(_http.Requests, r => r.Request.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("assistant/1-fix", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());

        var approval = Assert.Single(_host.Approvals.Requests);
        Assert.Equal("create_pull_request", approval.ToolName);

        var item = await _host.WithDbAsync(db => db.Items.Include(i => i.Artifacts).Include(i => i.Notes).SingleAsync(i => i.Id == itemId));
        var artifact = Assert.Single(item.Artifacts);
        Assert.Equal("pull_request", artifact.Kind);
        Assert.Equal("mcp:claude-code", artifact.AddedBy);
        Assert.Contains(item.Notes, n => n.Text.Contains("Opened pull request #14"));
    }

    [Fact]
    public async Task Nothing_is_sent_when_you_deny_the_action()
    {
        var itemId = await AddRepoAndItemAsync("web", "https://github.com/acme/web.git", SenderTrust.Organization);
        _host.Approvals.Answer = false;

        var result = await _client.CallToolAsync("create_pull_request", new Dictionary<string, object?>
        {
            ["repo"] = "web", ["sourceBranch"] = "b", ["title"] = "t", ["itemId"] = itemId,
        });

        Assert.True(result.IsError);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task External_items_always_ask_even_when_the_tool_is_set_to_allow()
    {
        await _host.Services.GetRequiredService<IToolPolicyStore>().SetOverrideAsync("comment", ToolPolicy.Allow);
        _http.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/comments"),
            """{"html_url":"https://github.com/acme/web/issues/3#c1"}""");

        var internalItem = await AddItemAsync("https://github.com/acme/web/issues/3", SenderTrust.Organization);
        await CallAsync("comment", new() { ["body"] = "On it.", ["itemId"] = internalItem });
        Assert.Empty(_host.Approvals.Requests);

        var externalItem = await AddItemAsync("https://github.com/acme/web/issues/3", SenderTrust.External);
        await CallAsync("comment", new() { ["body"] = "On it.", ["itemId"] = externalItem });
        var approval = Assert.Single(_host.Approvals.Requests);
        Assert.Contains("outside your organization", approval.Reason);
    }

    [Fact]
    public async Task Comments_on_jira_in_atlassian_document_format()
    {
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/OPS-12", SenderTrust.Organization);
        _http
            .OnUrl("issue/OPS-12?fields=reporter", """{"fields":{"reporter":{"accountId":"acc-dana","displayName":"Dana Smith"}}}""")
            .OnUrl("rest/api/3/myself", """{"accountId":"acc-me"}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/rest/api/3/issue/OPS-12/comment", """{"id":"1001"}""");

        var result = await CallAsync("comment", new() { ["body"] = "Done.\n\nPR: https://github.com/acme/web/pull/14", ["itemId"] = itemId });

        Assert.Contains("focusedCommentId=1001", result.GetProperty("url").GetString());
        Assert.Contains("tagged Dana Smith", result.GetProperty("summary").GetString());
        using var body = JsonDocument.Parse(_http.Requests.Single(r => r.Request.Method == HttpMethod.Post).Body!);
        var paragraphs = body.RootElement.GetProperty("body").GetProperty("content");
        Assert.Equal(2, paragraphs.GetArrayLength());
        Assert.Equal("doc", body.RootElement.GetProperty("body").GetProperty("type").GetString());
        var first = paragraphs[0].GetProperty("content");
        Assert.Equal("mention", first[0].GetProperty("type").GetString());
        Assert.Equal("acc-dana", first[0].GetProperty("attrs").GetProperty("id").GetString());
        Assert.Equal("@Dana Smith", first[0].GetProperty("attrs").GetProperty("text").GetString());
        Assert.Equal(" ", first[1].GetProperty("text").GetString());
        Assert.Equal("Done.", first[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Doesnt_tag_yourself_on_a_jira_issue_you_reported()
    {
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/OPS-12", SenderTrust.Organization);
        _http
            .OnUrl("issue/OPS-12?fields=reporter", """{"fields":{"reporter":{"accountId":"acc-me","displayName":"Sam Rivera"}}}""")
            .OnUrl("rest/api/3/myself", """{"accountId":"acc-me"}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/rest/api/3/issue/OPS-12/comment", """{"id":"1002"}""");

        await CallAsync("comment", new() { ["body"] = "Done.", ["itemId"] = itemId });

        using var body = JsonDocument.Parse(_http.Requests.Single(r => r.Request.Method == HttpMethod.Post).Body!);
        Assert.DoesNotContain("mention", body.RootElement.GetRawText());
    }

    [Fact]
    public async Task Moves_a_jira_issue_by_status_name_and_lists_options_when_it_cant()
    {
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/OPS-12", SenderTrust.Organization);
        _http
            .OnUrl("issue/OPS-12/transitions", """
                {"transitions":[{"id":"21","name":"Start work","to":{"name":"In Progress"}},{"id":"31","name":"Finish","to":{"name":"Done"}}]}
                """)
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/transitions"), "");

        var moved = await CallAsync("change_status", new() { ["status"] = "done", ["itemId"] = itemId });
        Assert.Contains("Moved OPS-12 to Done", moved.GetProperty("summary").GetString());
        Assert.Contains("\"31\"", _http.Requests.Last().Body);

        var failed = await _client.CallToolAsync("change_status", new Dictionary<string, object?> { ["status"] = "Blocked", ["itemId"] = itemId });
        Assert.True(failed.IsError);
        Assert.Contains("Start work (to In Progress)", Text(failed));
    }

    [Fact]
    public async Task Lists_the_statuses_a_jira_issue_can_move_to()
    {
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/OPS-12", SenderTrust.Organization);
        _http
            .OnUrl("issue/OPS-12?fields=status", """{"fields":{"status":{"name":"In Progress"}}}""")
            .OnUrl("issue/OPS-12/transitions", """
                {"transitions":[
                  {"id":"41","name":"Send to QA","to":{"name":"Ready for QA","statusCategory":{"name":"In Progress"}}},
                  {"id":"51","name":"Blocked","to":{"name":"Blocked","statusCategory":{"name":"To Do"}}},
                  {"id":"61","name":"Close","to":{"name":"Done","statusCategory":{"name":"Done"}}}]}
                """);

        var result = await CallAsync("list_statuses", new() { ["itemId"] = itemId });

        Assert.Equal("In Progress", result.GetProperty("current").GetString());
        var options = result.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal(["Ready for QA", "Blocked", "Done"], options.Select(o => o.GetProperty("status").GetString()));
        Assert.Equal("Send to QA", options[0].GetProperty("transition").GetString());
        Assert.Equal("Done", options[2].GetProperty("category").GetString());
        Assert.Empty(_host.Approvals.Requests);
    }

    [Fact]
    public async Task Requests_changes_on_a_bitbucket_pull_request()
    {
        _http
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/pullrequests/9/comments"), """{"links":{"html":{"href":"https://bitbucket.org/acme/reporting/pull-requests/9#c"}}}""")
            .On(r => r.RequestUri!.AbsolutePath.EndsWith("/pullrequests/9/request-changes"), "");

        await CallAsync("review_pull_request", new()
        {
            ["decision"] = "request_changes", ["body"] = "Please add a test.", ["url"] = "https://bitbucket.org/acme/reporting/pull-requests/9",
        });

        Assert.Equal(["/2.0/repositories/acme/reporting/pullrequests/9/comments", "/2.0/repositories/acme/reporting/pullrequests/9/request-changes"],
            _http.Requests.Select(r => r.Request.RequestUri!.AbsolutePath));
        Assert.Equal("Basic", _http.Requests[0].Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Opens_an_azure_devops_pull_request_on_the_default_branch()
    {
        await AddRepoAndItemAsync("api", "https://contoso@dev.azure.com/contoso/Platform/_git/api", SenderTrust.Organization);
        _http
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/contoso/Platform/_apis/git/repositories/api", """{"defaultBranch":"refs/heads/develop"}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/pullrequests"), """{"pullRequestId":77}""");

        var result = await CallAsync("create_pull_request", new() { ["repo"] = "api", ["sourceBranch"] = "feature/x", ["title"] = "X", ["draft"] = true });

        Assert.Equal("https://dev.azure.com/contoso/Platform/_git/api/pullrequest/77", result.GetProperty("url").GetString());
        using var body = JsonDocument.Parse(_http.Requests.Last().Body!);
        Assert.Equal("refs/heads/feature/x", body.RootElement.GetProperty("sourceRefName").GetString());
        Assert.Equal("refs/heads/develop", body.RootElement.GetProperty("targetRefName").GetString());
        Assert.True(body.RootElement.GetProperty("isDraft").GetBoolean());
        Assert.Equal("Bearer", _http.Requests.Last().Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task Drafts_an_email_reply_to_the_latest_message_from_someone_else()
    {
        var itemId = await _host.WithDbAsync(async db =>
        {
            db.Signals.AddRange(
                new Signal { Source = "outlook", ExternalId = "msg-from-dana", ThreadKey = "conv1", FromMe = false, ReceivedAt = DateTime.UtcNow.AddHours(-2) },
                new Signal { Source = "outlook", ExternalId = "msg-from-me", ThreadKey = "conv1", FromMe = true, ReceivedAt = DateTime.UtcNow.AddHours(-1) });
            var item = new Item { Title = "Send Dana the numbers", Source = "outlook", ExternalId = "mail:conv1", Trust = SenderTrust.Organization, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.Items.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        });
        _http.On(r => r.RequestUri!.AbsolutePath == "/v1.0/me/messages/msg-from-dana/createReplyAll",
            """{"id":"draft-1","webLink":"https://outlook.office.com/mail/drafts/id/draft-1"}""");
        _http.OnUrl("v1.0/me?$select", """{"displayName":"Sam Rivera","mail":"sam@example.com"}""");

        var result = await CallAsync("draft_email_reply", new() { ["itemId"] = itemId, ["body"] = "Hi Dana,\n\nHere they are & more." });

        Assert.Equal("email_draft", result.GetProperty("kind").GetString());
        Assert.Contains("Nothing was sent", result.GetProperty("summary").GetString());
        using var body = JsonDocument.Parse(_http.Requests.Single(r => r.Request.Method == HttpMethod.Post).Body!);
        Assert.Equal("Hi Dana,<br><br>Here they are &amp; more.<br><br>Thanks,<br>Sam", body.RootElement.GetProperty("comment").GetString());
    }

    [Theory]
    [InlineData("Sounds good.", "Sounds good.\n\nBest,\nMike")]
    [InlineData("Sounds good.\r\n\r\n\r\n\r\nSee you then.", "Sounds good.\n\nSee you then.\n\nBest,\nMike")]
    [InlineData("Sounds good.\n\nThanks,\nSam", "Sounds good.\n\nThanks,\nSam")]
    [InlineData("Sounds good.\n\nBest regards", "Sounds good.\n\nBest regards")]
    [InlineData("Thanks for the update, it helps.", "Thanks for the update, it helps.\n\nBest,\nMike")]
    public void Adds_a_sign_off_only_when_the_reply_has_none(string text, string expected) =>
        Assert.Equal(expected, Assistant.Actions.EmailBody.WithSignOff(text, "Best,\nMike"));

    [Fact]
    public async Task Posts_a_slack_reply_in_the_thread_and_always_asks_first()
    {
        // Even set to Allow, posting as you still asks.
        await _host.Services.GetRequiredService<IToolPolicyStore>().SetOverrideAsync("post_message", ToolPolicy.Allow);
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        var itemId = await AddChatItemAsync("slack", "slack:C9:1793000000.000100");
        _http
            .OnUrl("api/chat.postMessage", """{"ok":true,"ts":"1793000100.000200"}""")
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","url":"https://example.slack.com/"}""");

        var result = await CallAsync("post_message", new() { ["itemId"] = itemId, ["text"] = "Done <here> & live" });

        var approval = Assert.Single(_host.Approvals.Requests);
        Assert.Contains("speaks for you", approval.Reason);
        Assert.Contains("Done <here> & live", approval.ArgumentsJson);
        var form = Uri.UnescapeDataString(_http.Requests.First(r => r.Request.RequestUri!.AbsolutePath.EndsWith("chat.postMessage")).Body!.Replace('+', ' '));
        Assert.Contains("channel=C9", form);
        Assert.Contains("thread_ts=1793000000.000100", form);
        Assert.Contains("text=Done &lt;here&gt; &amp; live", form);
        Assert.Equal("https://example.slack.com/archives/C9/p1793000100000200?thread_ts=1793000000.000100&cid=C9", result.GetProperty("url").GetString());
    }

    [Fact]
    public async Task Tags_the_person_who_asked_in_a_slack_thread()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        var itemId = await AddChatItemAsync("slack", "slack:C9:1793000000.000100");
        await _host.WithDbAsync(async db =>
        {
            db.Signals.AddRange(
                new Signal { Source = "slack", ExternalId = "C9:1", ThreadKey = "C9:1793000000.000100", Author = "Dana", AuthorAddress = "U7", ReceivedAt = DateTime.UtcNow.AddHours(-3) },
                new Signal { Source = "slack", ExternalId = "C9:2", ThreadKey = "C9:1793000000.000100", Author = "Lee", AuthorAddress = "U8", ReceivedAt = DateTime.UtcNow.AddHours(-2) },
                new Signal { Source = "slack", ExternalId = "C9:3", ThreadKey = "C9:1793000000.000100", Author = "Sam", AuthorAddress = "U1", FromMe = true, ReceivedAt = DateTime.UtcNow.AddHours(-1) });
            await db.SaveChangesAsync();
            return 0;
        });
        _http
            .OnUrl("api/chat.postMessage", """{"ok":true,"ts":"1793000100.000200"}""")
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","url":"https://example.slack.com/"}""");

        var result = await CallAsync("post_message", new() { ["itemId"] = itemId, ["text"] = "It's live." });

        var form = Uri.UnescapeDataString(_http.Requests.First(r => r.Request.RequestUri!.AbsolutePath.EndsWith("chat.postMessage")).Body!.Replace('+', ' '));
        Assert.Contains("text=<@U8> It's live.", form);
        Assert.Contains("tagged Lee", result.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Posting_on_a_dm_topic_goes_to_the_dm_itself()
    {
        _host.Services.GetRequiredService<ISecretStore>().Set(SecretNames.SlackToken, "xoxp-test");
        var itemId = await AddChatItemAsync("slack", "slack:D1|638000");
        _http
            .OnUrl("api/chat.postMessage", """{"ok":true,"ts":"1793000100.000200"}""")
            .OnUrl("api/auth.test", """{"ok":true,"user_id":"U1","user":"sam","team_id":"T1","url":"https://example.slack.com/"}""");

        await CallAsync("post_message", new() { ["itemId"] = itemId, ["text"] = "Hi" });

        var form = Uri.UnescapeDataString(_http.Requests.First(r => r.Request.RequestUri!.AbsolutePath.EndsWith("chat.postMessage")).Body!);
        Assert.Contains("channel=D1&", form + "&");
        Assert.DoesNotContain("638000", form);
    }

    [Fact]
    public async Task Posting_respects_deny()
    {
        await _host.Services.GetRequiredService<IToolPolicyStore>().SetOverrideAsync("post_message", ToolPolicy.Deny);
        var itemId = await AddChatItemAsync("slack", "slack:D1");

        var result = await _client.CallToolAsync("post_message", new Dictionary<string, object?> { ["itemId"] = itemId, ["text"] = "Hi" });

        Assert.True(result.IsError);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task Replies_in_a_teams_channel_thread()
    {
        var itemId = await AddChatItemAsync("teams", "teams:19:abc@thread.tacv2:1712345678901");
        _http
            .OnUrl("me/joinedTeams", """{"value":[{"id":"T1","displayName":"Delivery"}]}""")
            .OnUrl("teams/T1/channels?$select", """{"value":[{"id":"19:abc@thread.tacv2","displayName":"General"}]}""")
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/replies"), """{"id":"r9","webUrl":"https://teams.microsoft.com/l/message/r9"}""");

        var result = await CallAsync("post_message", new() { ["itemId"] = itemId, ["text"] = "On it.\nWill update by 3." });

        var post = _http.Requests.Last();
        Assert.Equal("/v1.0/teams/T1/channels/19:abc@thread.tacv2/messages/1712345678901/replies", Uri.UnescapeDataString(post.Request.RequestUri!.AbsolutePath));
        using var body = JsonDocument.Parse(post.Body!);
        Assert.Equal("On it.<br>Will update by 3.", body.RootElement.GetProperty("body").GetProperty("content").GetString());
        Assert.Contains("Delivery › General", result.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task Handle_item_prompt_walks_through_the_workflow()
    {
        var prompts = await _client.ListPromptsAsync();
        Assert.Contains(prompts, p => p.Name == "handle_item");

        var prompt = await _client.GetPromptAsync("handle_item", new Dictionary<string, object?> { ["itemId"] = "142" });
        var text = Assert.IsType<TextContentBlock>(Assert.Single(prompt.Messages).Content).Text;

        Assert.Contains("get_work_context with itemId 142", text);
        Assert.Contains("create_pull_request", text);
        Assert.Contains("draft_email_reply", text);
        Assert.Contains("Wait for my go-ahead", text);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        Assert.False(result.IsError, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private Task<int> AddItemAsync(string url, SenderTrust trust) => _host.WithDbAsync(async db =>
    {
        var item = new Item { Title = "Work", Source = "test", SourceUrl = url, Trust = trust, Requester = "someone@example.com", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    });

    private Task<int> AddChatItemAsync(string source, string externalId) => _host.WithDbAsync(async db =>
    {
        var item = new Item { Title = "Reply", Source = source, ExternalId = externalId, Trust = SenderTrust.Organization, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    });

    private Task<int> AddRepoAndItemAsync(string name, string remote, SenderTrust trust) => _host.WithDbAsync(async db =>
    {
        db.RepoMappings.Add(new RepoMapping { Name = name, RemoteUrl = remote, LocalPath = $@"D:\src\{name}" });
        var item = new Item { Title = "Work", Source = "test", RepoHint = name, Trust = trust, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    });

    private sealed class NoCli : IGitHubCliToken
    {
        public bool IsAvailable => false;

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class NoAzureCli : IAzureCliToken
    {
        public bool IsAvailable => false;

        public Task<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class FakeTokens : IMicrosoftTokenProvider
    {
        public bool IsConfigured => true;

        public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) => Task.FromResult("ms-token");
    }
}
