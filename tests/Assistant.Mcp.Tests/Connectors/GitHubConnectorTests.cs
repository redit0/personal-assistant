using System.Net;
using Assistant.Connectors.GitHub;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Mcp.Tests.Connectors;

public sealed class GitHubConnectorTests : IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly InMemorySecretStore _secrets = new();
    private readonly FakeHttp _http = new();

    public GitHubConnectorTests()
    {
        _secrets.Set(SecretNames.GitHubToken, "ghp_test");
        _http
            .OnUrl("review-requested:@me", Results(Issue("acme/web", 12, "Fix login", "alice", "MEMBER", pr: true)))
            .OnUrl("assignee:@me", Results(
                Issue("acme/web", 12, "Fix login", "alice", "MEMBER", pr: true),
                Issue("acme/api", 7, "Crash on export", "bob", "COLLABORATOR")))
            .OnUrl("mentions:@me", Results(Issue("someone/lib", 3, "Question for you", "stranger", "NONE")))
            .OnUrl("author:@me", Results(Issue("acme/web", 20, "Add retries", "me", "OWNER", pr: true)));
    }

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Maps_each_query_to_the_right_kind_of_item()
    {
        var snapshot = await CreateConnector().FetchAsync(default);
        var byId = snapshot.Items.ToDictionary(i => i.ExternalId);

        Assert.Equal(4, snapshot.Items.Count);
        Assert.Empty(snapshot.FailedScopes);

        // The PR in both the review and assigned results is a review, the more urgent kind.
        var review = byId["github.com:acme/web#12"];
        Assert.Equal(ItemType.Review, review.Type);
        Assert.Equal("Review: Fix login", review.Title);
        Assert.Equal("alice", review.Requester);
        Assert.Equal(SenderTrust.Organization, review.Trust);
        Assert.Equal("web", review.RepoHint);
        Assert.Equal(Priority.High, review.Priority);

        Assert.Equal(ItemType.Assigned, byId["github.com:acme/api#7"].Type);

        var mention = byId["github.com:someone/lib#3"];
        Assert.Equal(ItemType.Ask, mention.Type);
        Assert.Equal(SenderTrust.External, mention.Trust);

        var mine = byId["github.com:acme/web#20"];
        Assert.Equal(ItemType.WaitingOn, mine.Type);
        Assert.Equal(SenderTrust.Self, mine.Trust);
        Assert.Null(mine.Requester);
    }

    [Fact]
    public async Task Sends_the_token_and_api_headers()
    {
        await CreateConnector().FetchAsync(default);

        var request = _http.Requests[0].Request;
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("ghp_test", request.Headers.Authorization?.Parameter);
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.NotEmpty(request.Headers.UserAgent);
    }

    [Fact]
    public async Task Partial_results_mark_the_scope_failed_so_nothing_closes()
    {
        _http.OnUrl("author:@me", """{"total_count":0,"incomplete_results":true,"items":[]}""");

        var snapshot = await CreateConnector().FetchAsync(default);

        Assert.Contains(GitHubConnector.Scope, snapshot.FailedScopes);
    }

    [Fact]
    public async Task A_rejected_token_gives_a_clear_error()
    {
        _http.On(_ => true, """{"message":"Bad credentials"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => CreateConnector().FetchAsync(default));
        Assert.Contains("rejected the token", ex.Message);
    }

    [Fact]
    public async Task A_secondary_rate_limit_says_so_instead_of_blaming_sso()
    {
        _http.On(_ => true, """{"documentation_url":"https://docs.github.com/rest/overview/rate-limits-for-the-rest-api#about-secondary-rate-limits","message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => CreateConnector().FetchAsync(default));
        Assert.Contains("too many arrived close together", ex.Message);
        Assert.Contains("next sync", ex.Message);
        Assert.DoesNotContain("SSO", ex.Message);
    }

    [Fact]
    public async Task A_plain_forbidden_still_points_at_sso()
    {
        _http.On(_ => true, """{"message":"Resource protected by organization SAML enforcement."}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => CreateConnector().FetchAsync(default));
        Assert.Contains("SSO", ex.Message);
    }

    [Fact]
    public void Not_ready_until_enabled_with_a_token()
    {
        var settings = new UserSettingsStore(_settingsFile);
        var connector = new GitHubConnector(_http, settings, new InMemorySecretStore(), new NoCli(), TimeProvider.System, NullLogger<GitHubConnector>.Instance);

        Assert.Equal("Turned off.", connector.NotReadyReason());
        settings.Update(s => s.GitHub.Enabled = true);
        Assert.Contains("token", connector.NotReadyReason());
    }

    private GitHubConnector CreateConnector()
    {
        var settings = new UserSettingsStore(_settingsFile);
        settings.Update(s => s.GitHub.Enabled = true);
        return new GitHubConnector(_http, settings, _secrets, new NoCli(), TimeProvider.System, NullLogger<GitHubConnector>.Instance);
    }

    private static string Results(params string[] items) =>
        $$"""{"total_count":{{items.Length}},"incomplete_results":false,"items":[{{string.Join(",", items)}}]}""";

    private static string Issue(string repo, int number, string title, string author, string association, bool pr = false) => $$"""
        {
          "number": {{number}},
          "title": "{{title}}",
          "html_url": "https://github.com/{{repo}}/{{(pr ? "pull" : "issues")}}/{{number}}",
          "repository_url": "https://api.github.com/repos/{{repo}}",
          "user": { "login": "{{author}}" },
          "author_association": "{{association}}",
          "updated_at": "2026-10-20T15:00:00Z",
          "body": "Details for {{title}}"
          {{(pr ? """, "pull_request": { "url": "x" }""" : "")}}
        }
        """;

    private sealed class NoCli : IGitHubCliToken
    {
        public bool IsAvailable => false;

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
