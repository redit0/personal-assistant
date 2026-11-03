using System.Net;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Items;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Mcp.Tests.Connectors;

public sealed class AzureDevOpsConnectorTests : IDisposable
{
    private const string Me = "aaaa-me";

    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly FakeHttp _http = new();
    private readonly FakeAzureCli _azureCli = new();
    private readonly InMemorySecretStore _secrets = new();
    private readonly UserSettingsStore _settings;

    public AzureDevOpsConnectorTests()
    {
        _settings = new UserSettingsStore(_settingsFile);
        _settings.Update(s =>
        {
            s.AzureDevOps.Enabled = true;
            s.AzureDevOps.WatchNewOrganizations = true;
            s.AzureDevOps.Organizations["acme"] = true;
            s.AzureDevOps.Organizations["oldorg"] = false;
        });

        _http
            .OnUrl("profiles/me", """{"id":"member-1","publicAlias":"member-1"}""")
            .OnUrl("_apis/accounts?memberId=member-1", """{"count":3,"value":[{"accountName":"acme"},{"accountName":"oldorg"},{"accountName":"newco"}]}""")
            .OnUrl("_apis/connectionData", $$$"""{"authenticatedUser":{"id":"{{{Me}}}"}}""")
            .OnUrl($"searchCriteria.reviewerId={Me}", $$"""
                {"value":[
                  {{Pr(41, "Add caching", "Dana", "dana-id", myVote: 0)}},
                  {{Pr(42, "Already voted", "Sam", "sam-id", myVote: 10)}}
                ]}
                """)
            .OnUrl($"searchCriteria.creatorId={Me}", $$"""{"value":[{{Pr(50, "My refactor", "Me", Me, myVote: 0)}}]}""")
            .On(r => r.Method == HttpMethod.Post && Body(r).Contains("[System.AssignedTo] = @Me"), """{"workItems":[{"id":100}]}""")
            .On(r => r.Method == HttpMethod.Post && Body(r).Contains("@RecentMentions"), """{"workItems":[{"id":200}]}""")
            .OnUrl("_apis/wit/workitems?ids=", """
                {"value":[
                  {"id":100,"fields":{"System.Title":"Update runbook","System.State":"Active","System.WorkItemType":"Task","System.TeamProject":"Ops",
                    "System.CreatedBy":{"displayName":"Dana"},"System.ChangedDate":"2026-10-20T10:00:00Z","Microsoft.VSTS.Common.Priority":1,
                    "Microsoft.VSTS.Scheduling.DueDate":"2026-11-01T17:00:00Z","System.Description":"<p>Steps &amp; notes</p>"}},
                  {"id":200,"fields":{"System.Title":"Question about deploy","System.State":"New","System.WorkItemType":"Bug","System.TeamProject":"Ops",
                    "System.ChangedDate":"2026-10-21T10:00:00Z"}}
                ]}
                """);
    }

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public async Task Discovers_new_organizations_and_respects_the_watch_setting()
    {
        await Connector().RefreshOrganizationsAsync(default);

        var orgs = _settings.Current.AzureDevOps.Organizations;
        Assert.True(orgs["acme"]);
        Assert.False(orgs["oldorg"]); // Your earlier choice is kept.
        Assert.True(orgs["newco"]);   // New, and WatchNewOrganizations is on.
    }

    [Fact]
    public async Task Maps_reviews_my_prs_assigned_and_mentioned_work_items()
    {
        _settings.Update(s => s.AzureDevOps.WatchNewOrganizations = false);

        var snapshot = await Connector().FetchAsync(default);
        var byId = snapshot.Items.ToDictionary(i => i.ExternalId);

        Assert.Empty(snapshot.FailedScopes);
        Assert.Equal(4, snapshot.Items.Count); // PR 42 is skipped because you already voted.

        var review = byId["acme:pr:41"];
        Assert.Equal(ItemType.Review, review.Type);
        Assert.Equal("https://dev.azure.com/acme/Platform/_git/api/pullrequest/41", review.Url);
        Assert.Equal("api", review.RepoHint);
        Assert.Equal("Dana", review.Requester);

        Assert.Equal(ItemType.WaitingOn, byId["acme:pr:50"].Type);

        var task = byId["acme:wi:100"];
        Assert.Equal(ItemType.Assigned, task.Type);
        Assert.Equal("Task 100: Update runbook", task.Title);
        Assert.Equal(Priority.High, task.Priority);
        Assert.Equal(new DateTime(2026, 11, 1, 17, 0, 0, DateTimeKind.Utc), task.DueAt);
        Assert.Equal("Steps & notes", task.SourceContent);
        Assert.Equal("https://dev.azure.com/acme/Ops/_workitems/edit/100", task.Url);

        Assert.Equal(ItemType.Ask, byId["acme:wi:200"].Type);

        // Unwatched organizations aren't queried at all.
        Assert.DoesNotContain(_http.Requests, r => r.Request.RequestUri!.Host == "dev.azure.com" && r.Request.RequestUri.AbsolutePath.StartsWith("/oldorg"));
        Assert.All(_http.Requests, r => Assert.Equal("Bearer", r.Request.Headers.Authorization?.Scheme));
    }

    [Fact]
    public async Task Falls_back_to_per_project_queries_when_the_org_level_list_is_missing()
    {
        _settings.Update(s => s.AzureDevOps.WatchNewOrganizations = false);
        _http
            .On(r => r.RequestUri!.AbsolutePath == "/acme/_apis/git/pullrequests", "{}", HttpStatusCode.NotFound)
            .OnUrl("/acme/_apis/projects", """{"value":[{"name":"Platform"}]}""")
            .On(r => r.RequestUri!.AbsolutePath == "/acme/Platform/_apis/git/pullrequests" && r.RequestUri.Query.Contains("reviewerId"),
                $$"""{"value":[{{Pr(41, "Add caching", "Dana", "dana-id", myVote: 0)}}]}""")
            .On(r => r.RequestUri!.AbsolutePath == "/acme/Platform/_apis/git/pullrequests" && r.RequestUri.Query.Contains("creatorId"),
                """{"value":[]}""");

        var snapshot = await Connector().FetchAsync(default);

        Assert.Contains(snapshot.Items, i => i.ExternalId == "acme:pr:41");
        Assert.Empty(snapshot.FailedScopes);
    }

    [Fact]
    public async Task A_failing_organization_is_reported_without_failing_the_others()
    {
        _settings.Update(s => s.AzureDevOps.Organizations["newco"] = true);
        _http.On(r => r.RequestUri!.AbsolutePath.StartsWith("/newco/"), "{}", HttpStatusCode.Forbidden);

        var snapshot = await Connector().FetchAsync(default);

        Assert.Equal(["newco"], snapshot.FailedScopes);
        Assert.Contains(snapshot.Items, i => i.Scope == "acme");
    }

    private AzureDevOpsConnector Connector() =>
        new(_http, _settings, new FakeTokens(), _azureCli, _secrets, TimeProvider.System, NullLogger<AzureDevOpsConnector>.Instance);

    private static string Body(HttpRequestMessage request) =>
        request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "";

    private static string Pr(int id, string title, string author, string authorId, int myVote) => $$"""
        {
          "pullRequestId": {{id}},
          "title": "{{title}}",
          "isDraft": false,
          "creationDate": "2026-10-19T08:00:00Z",
          "description": "About {{title}}",
          "createdBy": { "id": "{{authorId}}", "displayName": "{{author}}" },
          "repository": { "name": "api", "project": { "name": "Platform" } },
          "reviewers": [ { "id": "{{Me}}", "vote": {{myVote}} } ]
        }
        """;

    [Fact]
    public async Task Discovers_organizations_in_client_tenants_through_the_azure_cli()
    {
        _settings.Update(s => s.AzureDevOps.Tenants.Add(new AzureDevOpsTenant { TenantId = "contoso.onmicrosoft.com", Label = "Contoso" }));
        _http
            .On(r => r.RequestUri!.ToString().Contains("profiles/me") && Auth(r) == "Bearer cli-contoso.onmicrosoft.com",
                """{"id":"guest-1"}""")
            .On(r => r.RequestUri!.ToString().Contains("memberId=guest-1"),
                """{"value":[{"accountName":"contoso-eng"}]}""");

        var result = await Connector().RefreshOrganizationsAsync(default);

        Assert.Empty(result.Errors);
        var ado = _settings.Current.AzureDevOps;
        Assert.Equal(AzureDevOpsSettings.TenantSource("contoso.onmicrosoft.com"), ado.OrganizationSources["contoso-eng"]);
        Assert.Equal(AzureDevOpsSettings.HomeSource, ado.OrganizationSources["acme"]);
        Assert.Equal(["contoso.onmicrosoft.com"], _azureCli.Requested.Distinct());
    }

    [Fact]
    public async Task A_client_tenant_that_needs_az_login_is_reported_without_losing_the_home_tenant()
    {
        _settings.Update(s => s.AzureDevOps.Tenants.Add(new AzureDevOpsTenant { TenantId = "fabrikam.com" }));
        _azureCli.Fail = true;

        var result = await Connector().RefreshOrganizationsAsync(default);

        Assert.Equal(3, result.Found); // The home tenant's organizations still load.
        Assert.Contains(result.Errors, e => e.Contains("az login --tenant fabrikam.com"));
    }

    [Fact]
    public async Task Organizations_use_their_own_sign_in_when_syncing()
    {
        _secrets.Set(SecretNames.AzureDevOpsToken("tokenorg"), "my-pat");
        _settings.Update(s =>
        {
            s.AzureDevOps.WatchNewOrganizations = false;
            s.AzureDevOps.Tenants.Add(new AzureDevOpsTenant { TenantId = "contoso.onmicrosoft.com" });
            s.AzureDevOps.Organizations["contoso-eng"] = true;
            s.AzureDevOps.OrganizationSources["contoso-eng"] = AzureDevOpsSettings.TenantSource("contoso.onmicrosoft.com");
            s.AzureDevOps.Organizations["tokenorg"] = true;
            s.AzureDevOps.OrganizationSources["tokenorg"] = AzureDevOpsSettings.TokenSource;
        });

        await Connector().FetchAsync(default);

        string AuthFor(string org) => _http.Requests
            .Where(r => r.Request.RequestUri!.AbsolutePath.StartsWith($"/{org}/_apis/connectionData"))
            .Select(r => Auth(r.Request))
            .Single();

        Assert.Equal("Bearer ado-token", AuthFor("acme"));
        Assert.Equal("Bearer cli-contoso.onmicrosoft.com", AuthFor("contoso-eng"));
        Assert.Equal("Basic " + Convert.ToBase64String(":my-pat"u8.ToArray()), AuthFor("tokenorg"));
    }

    private static string? Auth(HttpRequestMessage request) =>
        request.Headers.Authorization is { } a ? $"{a.Scheme} {a.Parameter}" : null;

    private sealed class FakeAzureCli : IAzureCliToken
    {
        public bool Fail { get; set; }

        public List<string> Requested { get; } = [];

        public bool IsAvailable => true;

        public Task<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken)
        {
            Requested.Add(tenantId);
            return Fail
                ? throw new Core.Connectors.ConnectorException($"The Azure CLI isn't signed in to {tenantId}, or the sign-in expired. Run: az login --tenant {tenantId}")
                : Task.FromResult($"cli-{tenantId}");
        }
    }

    private sealed class FakeTokens : IMicrosoftTokenProvider
    {
        public bool IsConfigured => true;

        public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) =>
            Task.FromResult("ado-token");
    }
}
