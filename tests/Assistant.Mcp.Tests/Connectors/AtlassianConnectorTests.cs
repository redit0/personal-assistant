using System.Text;
using System.Text.Json;
using Assistant.Connectors.Atlassian;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Mcp.Tests.Connectors;

public sealed class AtlassianConnectorTests : IDisposable
{
    private const string MyUuid = "{11111111-2222-3333-4444-555555555555}";

    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeHttp _http = new();
    private readonly UserSettingsStore _settings;

    public AtlassianConnectorTests()
    {
        _settings = new UserSettingsStore(_settingsFile);
        _settings.Update(s =>
        {
            s.Atlassian.Email = "me@example.com";
            s.Atlassian.JiraEnabled = true;
            s.Atlassian.JiraSiteUrl = "https://example.atlassian.net";
            s.Atlassian.BitbucketEnabled = true;
        });
        _secrets.Set(SecretNames.JiraApiToken, "jira-token");
        _secrets.Set(SecretNames.BitbucketApiToken, "bb-token");
    }

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Jira_maps_assigned_and_reported_issues()
    {
        _http
            .On(r => Body(r).Contains("assignee = currentUser() AND statusCategory"), $$"""
                {"isLast":true,"issues":[{{Issue("OPS-12", "Rotate certs", "2026-11-03", "High", "Dana", "Me")}}]}
                """)
            .On(r => Body(r).Contains("reporter = currentUser()"), $$"""
                {"isLast":true,"issues":[{{Issue("OPS-40", "Fix VPN", null, "Medium", "Me", "Sam")}}]}
                """);

        var snapshot = await Jira().FetchAsync(default);
        var byId = snapshot.Items.ToDictionary(i => i.ExternalId);

        var assigned = byId["example.atlassian.net:OPS-12"];
        Assert.Equal(ItemType.Assigned, assigned.Type);
        Assert.Equal("OPS-12: Rotate certs", assigned.Title);
        Assert.Equal("https://example.atlassian.net/browse/OPS-12", assigned.Url);
        Assert.Equal(Priority.High, assigned.Priority);
        Assert.Equal("Dana", assigned.Requester);
        Assert.Equal(new DateTime(2026, 11, 3, 17, 0, 0), assigned.DueAt!.Value.ToLocalTime());
        Assert.Equal("Please rotate the certs before Friday.", assigned.SourceContent);

        var reported = byId["example.atlassian.net:OPS-40"];
        Assert.Equal(ItemType.WaitingOn, reported.Type);
        Assert.Equal("Sam", reported.Requester);
        Assert.Null(reported.DueAt);
    }

    [Fact]
    public async Task Jira_uses_the_new_search_endpoint_with_basic_auth_and_pages()
    {
        _http
            .On(r => Body(r).Contains("assignee = currentUser() AND statusCategory") && !Body(r).Contains("page2"),
                $$"""{"isLast":false,"nextPageToken":"page2","issues":[{{Issue("A-1", "One", null, "Low", "x", "Me")}}]}""")
            .On(r => Body(r).Contains("page2"),
                $$"""{"isLast":true,"issues":[{{Issue("A-2", "Two", null, "Low", "x", "Me")}}]}""")
            .On(r => Body(r).Contains("reporter = currentUser()"), """{"isLast":true,"issues":[]}""");

        var snapshot = await Jira().FetchAsync(default);

        Assert.Equal(2, snapshot.Items.Count);
        var request = _http.Requests[0].Request;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.atlassian.net/rest/api/3/search/jql", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("me@example.com:jira-token", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Jira_limits_both_queries_to_open_sprints_when_asked(bool activeSprintOnly)
    {
        _settings.Update(s => s.Atlassian.JiraActiveSprintOnly = activeSprintOnly);
        _http.On(r => r.RequestUri!.AbsolutePath.EndsWith("/search/jql"), """{"isLast":true,"issues":[]}""");

        await Jira().FetchAsync(default);

        var bodies = _http.Requests.Select(r => r.Body!).ToList();
        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(activeSprintOnly, b.Contains("sprint in openSprints()")));

        // The filter has to come before ORDER BY, or Jira rejects the query.
        Assert.All(bodies, b => Assert.DoesNotContain("ORDER BY updated DESC AND", b));
    }

    [Fact]
    public async Task Bitbucket_finds_reviews_waiting_on_me_and_my_prs()
    {
        _http
            .OnUrl("2.0/user", $$"""{"uuid":"{{MyUuid}}","display_name":"Me"}""")
            .OnUrl("2.0/user/workspaces", """{"values":[{"workspace":{"slug":"acme"}}]}""")
            .OnUrl("repositories/acme?role=member", """{"values":[{"full_name":"acme/reporting"},{"full_name":"acme/web"}]}""")
            .OnUrl("repositories/acme/reporting/pullrequests", $$"""
                {"values":[{{Pr(7, "acme/reporting", "Raise timeout", "Dana", approvedByMe: false)}}]}
                """)
            .OnUrl("repositories/acme/web/pullrequests", $$"""
                {"values":[{{Pr(3, "acme/web", "Already approved", "Sam", approvedByMe: true)}}]}
                """)
            .OnUrl("workspaces/acme/pullrequests/", $$"""
                {"values":[{{Pr(9, "acme/web", "My change", "Me", approvedByMe: false)}}]}
                """);

        var snapshot = await Bitbucket().FetchAsync(default);
        var byId = snapshot.Items.ToDictionary(i => i.ExternalId);

        Assert.Equal(2, snapshot.Items.Count);
        var review = byId["acme:acme/reporting#7"];
        Assert.Equal(ItemType.Review, review.Type);
        Assert.Equal("Review: Raise timeout", review.Title);
        Assert.Equal("reporting", review.RepoHint);
        Assert.Equal("Dana", review.Requester);
        Assert.Equal("Body of Raise timeout", review.SourceContent);
        Assert.Equal(ItemType.WaitingOn, byId["acme:acme/web#9"].Type);

        var reviewQuery = _http.Requests.Select(r => Uri.UnescapeDataString(r.Request.RequestUri!.ToString()))
            .First(u => u.Contains("reporting/pullrequests"));
        Assert.Contains($"reviewers.uuid=\"{MyUuid}\"", reviewQuery);
        Assert.Contains("state=\"OPEN\"", reviewQuery);
    }

    [Fact]
    public async Task Bitbucket_marks_a_failing_workspace_so_its_items_stay_open()
    {
        _http
            .OnUrl("2.0/user", $$"""{"uuid":"{{MyUuid}}"}""")
            .OnUrl("2.0/user/workspaces", """{"values":[{"workspace":{"slug":"acme"}},{"workspace":{"slug":"broken"}}]}""")
            .OnUrl("repositories/acme?role=member", """{"values":[]}""")
            .OnUrl("workspaces/acme/pullrequests/", """{"values":[]}""")
            .OnUrl("repositories/broken", """{"error":"nope"}""", System.Net.HttpStatusCode.InternalServerError);

        var snapshot = await Bitbucket().FetchAsync(default);

        Assert.Equal(["broken"], snapshot.FailedScopes);
    }

    [Fact]
    public void Adf_is_flattened_to_text()
    {
        using var doc = JsonDocument.Parse("""
            {"type":"doc","version":1,"content":[
              {"type":"paragraph","content":[{"type":"text","text":"Hi "},{"type":"mention","attrs":{"text":"@Dana"}},{"type":"text","text":","}]},
              {"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph","content":[{"type":"text","text":"first"}]}]}]}
            ]}
            """);

        var text = JiraConnector.AdfToText(doc.RootElement);

        Assert.Contains("Hi @Dana,", text);
        Assert.Contains("first", text);
    }

    private JiraConnector Jira() => new(_http, _settings, _secrets, TimeProvider.System);

    private BitbucketConnector Bitbucket() =>
        new(_http, _settings, _secrets, TimeProvider.System, NullLogger<BitbucketConnector>.Instance);

    private static string Body(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

    private static string Issue(string key, string summary, string? due, string priority, string reporter, string assignee) => $$"""
        {
          "key": "{{key}}",
          "fields": {
            "summary": "{{summary}}",
            "status": { "name": "In Progress", "statusCategory": { "key": "indeterminate" } },
            "duedate": {{(due is null ? "null" : $"\"{due}\"")}},
            "priority": { "name": "{{priority}}" },
            "reporter": { "displayName": "{{reporter}}" },
            "assignee": { "displayName": "{{assignee}}" },
            "issuetype": { "name": "Task" },
            "updated": "2026-10-20T10:00:00.000-0500",
            "description": {"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"Please rotate the certs before Friday."}]}]}
          }
        }
        """;

    private static string Pr(int id, string repo, string title, string author, bool approvedByMe) => $$"""
        {
          "id": {{id}},
          "title": "{{title}}",
          "author": { "display_name": "{{author}}" },
          "destination": { "repository": { "full_name": "{{repo}}" } },
          "links": { "html": { "href": "https://bitbucket.org/{{repo}}/pull-requests/{{id}}" } },
          "updated_on": "2026-10-21T09:00:00+00:00",
          "summary": { "raw": "Body of {{title}}" },
          "participants": [ { "user": { "uuid": "{{MyUuid}}" }, "role": "REVIEWER", "approved": {{(approvedByMe ? "true" : "false")}}, "state": {{(approvedByMe ? "\"approved\"" : "null")}} } ]
        }
        """;
}
