using System.Text.Json;
using Assistant.Actions;
using Assistant.Actions.Shipping;
using Assistant.Connectors.GitHub;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Items;
using Assistant.Core.Repos;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Assistant.Mcp.Tests.Connectors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assistant.Mcp.Tests;

/// <summary>Merging, deployment discovery and tracking, and assignment, against fake Bitbucket, Jira, and Azure APIs.</summary>
public sealed class ShippingToolsTests : IAsyncLifetime, IDisposable
{
    private const string Sites = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Web/sites/";
    private const string MergeCommit = "abc1234def5678abc1234def5678abc1234def56";

    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private readonly string _clone = Directory.CreateTempSubdirectory("pa-clone-").FullName;
    private readonly FakeHttp _http = new();
    private McpTestHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var settings = new UserSettingsStore(_settingsFile);
        settings.Update(s => s.Atlassian.Email = "sam@example.com");

        _host = await McpTestHost.StartAsync(services =>
        {
            services.AddSingleton(settings);
            services.AddSingleton<IHttpClientFactory>(_http);
            services.AddSingleton<IGitHubCliToken, NoGitHubCli>();
            services.AddSingleton<IMicrosoftTokenProvider, FakeTokens>();
            services.AddSingleton<IAzureCliToken, FakeAzureCli>();
            services.AddSingleton<GitHubConnector>();
            services.AddSingleton<AzureDevOpsConnector>();
            services.AddSingleton<GraphClient>();
            services.AddSingleton<AzureAppServices>();
            services.AddSingleton<Assistant.Connectors.Slack.SlackClient>();
            services.AddAssistantActions();
        });

        var secrets = _host.Services.GetRequiredService<ISecretStore>();
        secrets.Set(SecretNames.JiraApiToken, "jira");
        secrets.Set(SecretNames.BitbucketApiToken, "bb");

        await _host.WithDbAsync(async db =>
        {
            db.RepoMappings.Add(new RepoMapping { Name = "recordscore", RemoteUrl = "git@bitbucket.org:acmedev/recordscore.git", LocalPath = _clone });
            return await db.SaveChangesAsync();
        });

        FakeAzure();
        _host.Approvals.Answer = true;
        _client = await _host.ConnectAsync("claude-code");
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
    }

    public void Dispose()
    {
        File.Delete(_settingsFile);
        Directory.Delete(_clone, recursive: true);
    }

    [Fact]
    public async Task Finds_the_qa_target_from_deployment_center_and_marks_prod_off_limits()
    {
        var map = await CallAsync("find_deployments", new() { ["repo"] = "recordscore" });

        var qa = map.GetProperty("qaTarget");
        Assert.Equal("uat", qa.GetProperty("branch").GetString());
        Assert.Equal("contoso-records-uat", qa.GetProperty("app").GetString());
        Assert.Equal("https://contoso-records-uat.azurewebsites.net", qa.GetProperty("url").GetString());
        Assert.Equal("deployment-center", qa.GetProperty("foundBy").GetString());
        Assert.Equal(["prod"], map.GetProperty("productionBranches").EnumerateArray().Select(b => b.GetString()));

        // Every app's Deployment Center source is checked, so one whose name has nothing to do with the repo is still found.
        // An app that deploys from another repo isn't offered as a candidate.
        var other = map.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("app").GetString() == "other-app");
        Assert.Equal("kiosk-preview", other.GetProperty("branch").GetString());
        Assert.Empty(map.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task Merges_into_the_qa_branch_and_says_where_it_deploys()
    {
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/PROJ-40");
        FakePull(7, "uat");
        _http.On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/pullrequests/7/merge"), $$$"""{"merge_commit":{"hash":"{{{MergeCommit}}}"}}""");

        var result = await CallAsync("merge_pull_request", new()
        {
            ["url"] = "https://bitbucket.org/acmedev/recordscore/pull-requests/7", ["itemId"] = itemId,
        });

        Assert.Contains("uat deploys to contoso-records-uat", result.GetProperty("summary").GetString());
        var merge = Assert.Single(_http.Requests, r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.Host == "api.bitbucket.org");
        using var body = JsonDocument.Parse(merge.Body!);
        Assert.True(body.RootElement.GetProperty("close_source_branch").GetBoolean());

        var notes = await _host.WithDbAsync(db => db.ItemNotes.Where(n => n.ItemId == itemId).Select(n => n.Text).ToListAsync());
        Assert.Contains(notes, n => n.StartsWith("Merged acmedev/recordscore pull request #7"));
    }

    [Fact]
    public async Task Never_merges_into_a_branch_that_deploys_production()
    {
        FakePull(8, "prod");

        var result = await _client.CallToolAsync("merge_pull_request", new Dictionary<string, object?>
        {
            ["url"] = "https://bitbucket.org/acmedev/recordscore/pull-requests/8",
        });

        Assert.True(result.IsError);
        Assert.Contains("deploys production", Text(result));
        Assert.DoesNotContain(_http.Requests, r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.Host == "api.bitbucket.org");
    }

    [Fact]
    public async Task Waits_for_failing_or_running_checks_before_merging()
    {
        FakePull(9, "uat", checkState: "INPROGRESS");

        var result = await _client.CallToolAsync("merge_pull_request", new Dictionary<string, object?>
        {
            ["url"] = "https://bitbucket.org/acmedev/recordscore/pull-requests/9",
        });

        Assert.True(result.IsError);
        Assert.Contains("still running", Text(result));
        Assert.DoesNotContain(_http.Requests, r => r.Request.Method == HttpMethod.Post && r.Request.RequestUri!.Host == "api.bitbucket.org");
    }

    [Fact]
    public async Task Follows_a_merged_commit_to_a_finished_deployment()
    {
        _http.OnUrl($"commit/{MergeCommit}/statuses", """{"values":[{"name":"build","state":"SUCCESSFUL"}]}""");

        var status = await CallAsync("get_deployment_status", new()
        {
            ["repo"] = "recordscore", ["commit"] = MergeCommit, ["branch"] = "uat",
        });

        Assert.Equal("deployed", status.GetProperty("stage").GetString());
        Assert.Equal("contoso-records-uat", status.GetProperty("app").GetString());
        Assert.Equal(200, status.GetProperty("siteStatusCode").GetInt32());
        Assert.Equal("succeeded", status.GetProperty("deployment").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Assigns_a_jira_ticket_back_to_the_reporter()
    {
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/PROJ-40");
        _http
            .OnUrl("issue/PROJ-40?fields=reporter", """{"fields":{"reporter":{"accountId":"acc-dana","displayName":"Dana Lee"}}}""")
            .On(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath.EndsWith("/issue/PROJ-40/assignee"), "");

        var result = await CallAsync("assign_ticket", new() { ["itemId"] = itemId });

        Assert.Equal("Assigned PROJ-40 to Dana Lee.", result.GetProperty("summary").GetString());
        var put = Assert.Single(_http.Requests, r => r.Request.Method == HttpMethod.Put);
        Assert.Contains("\"accountId\":\"acc-dana\"", put.Body);
    }

    [Fact]
    public async Task A_saved_target_is_used_but_cannot_unmark_production()
    {
        await CallAsync("save_deployment_target", new()
        {
            ["repo"] = "recordscore", ["branch"] = "prod", ["app"] = "contoso-records-prod", ["environment"] = "dev",
        });

        var map = await CallAsync("find_deployments", new() { ["repo"] = "recordscore" });

        Assert.Contains("prod", map.GetProperty("productionBranches").EnumerateArray().Select(b => b.GetString()));
        Assert.Equal("uat", map.GetProperty("qaTarget").GetProperty("branch").GetString());
    }

    [Fact]
    public void Reads_branches_and_apps_from_pipeline_files()
    {
        var workflows = Directory.CreateDirectory(Path.Combine(_clone, ".github", "workflows")).FullName;
        File.WriteAllText(Path.Combine(workflows, "main_tc-app-dev.yml"), """
            on:
              push:
                branches:
                  - main
            jobs:
              deploy:
                steps:
                  - uses: azure/webapps-deploy@v3
                    with:
                      app-name: 'tc-app-dev'
            """);
        File.WriteAllText(Path.Combine(_clone, "azure-pipelines.yml"), """
            trigger:
            - master
            stages:
            - stage: Stage
              jobs:
              - deployment: DeployStage
                steps:
                - task: AzureRmWebAppDeployment@4
                  inputs:
                    appName: 'websitestage-core'
            - stage: Prod
              jobs:
              - deployment: DeployProd
                steps:
                - task: AzureRmWebAppDeployment@4
                  inputs:
                    appName: 'websiteprod-core'
            """);

        var github = DeploymentFinder.ParsePipelineFile(Path.Combine(workflows, "main_tc-app-dev.yml"), "x").Single();
        Assert.Equal(("main", "tc-app-dev"), (github.Branch, github.App));
        Assert.Equal("dev", DeploymentFinder.Classify(github.App, github.Branch));

        // One branch deploying stage and then prod: a staged pipeline, where prod is a later stage behind its approval gates.
        var ado = DeploymentFinder.ParsePipelineFile(Path.Combine(_clone, "azure-pipelines.yml"), "x").ToList();
        Assert.Equal(["websitestage-core", "websiteprod-core"], ado.Select(d => d.App));
        Assert.Equal([false, true], ado.Select(d => d.LaterStage));
        Assert.All(ado, d => Assert.Equal("master", d.Branch));
        Assert.Equal(["uat", "prod"], ado.Select(d => DeploymentFinder.Classify(d.App, d.Branch)));
    }

    [Fact]
    public async Task A_staged_pipeline_branch_is_mergeable_because_later_stages_wait_for_approval()
    {
        File.WriteAllText(Path.Combine(_clone, "azure-pipelines.yml"), """
            trigger:
            - master
            stages:
            - stage: Dev
              jobs:
              - deployment: DeployDev
                steps:
                - task: AzureWebApp@1
                  inputs:
                    appName: 'records-dev'
            - stage: Prod
              jobs:
              - deployment: DeployProd
                steps:
                - task: AzureWebApp@1
                  inputs:
                    appName: 'records-prod'
            """);

        var map = await CallAsync("find_deployments", new() { ["repo"] = "recordscore" });

        var qa = map.GetProperty("qaTarget");
        Assert.Equal(("master", "records-dev"), (qa.GetProperty("branch").GetString(), qa.GetProperty("app").GetString()));
        Assert.DoesNotContain("master", map.GetProperty("productionBranches").EnumerateArray().Select(b => b.GetString()));
        var gated = map.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("app").GetString() == "records-prod");
        Assert.True(gated.GetProperty("behindApproval").GetBoolean());
    }

    [Fact]
    public async Task A_feature_slot_repo_has_no_qa_branch_and_its_production_branch_is_off_limits()
    {
        UseFeatureSlots();
        FakePull(10, "master");

        var map = await CallAsync("find_deployments", new() { ["repo"] = "recordscore" });
        var merge = await _client.CallToolAsync("merge_pull_request", new Dictionary<string, object?>
        {
            ["url"] = "https://bitbucket.org/acmedev/recordscore/pull-requests/10",
        });

        Assert.Equal("feature-slot", map.GetProperty("flow").GetString());
        Assert.Equal("contoso-records-uat", map.GetProperty("featureSlotApp").GetString());
        Assert.False(map.TryGetProperty("qaTarget", out var qa) && qa.ValueKind != JsonValueKind.Null);
        Assert.Contains("master", map.GetProperty("productionBranches").EnumerateArray().Select(b => b.GetString()));
        Assert.True(merge.IsError);
        Assert.Contains("deploys production", Text(merge));
    }

    [Fact]
    public async Task Creates_a_slot_named_after_the_feature_branch_and_points_it_at_the_branch()
    {
        UseFeatureSlots();
        var itemId = await AddItemAsync("https://example.atlassian.net/browse/PROJ-12");
        const string slot = Sites + "contoso-records-uat/slots/proj-12";
        _http
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == Sites + "contoso-records-uat",
                """{"location":"West US","properties":{"serverFarmId":"/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan"}}""")
            .On(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == slot, "{}")
            .On(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == slot,
                """{"properties":{"state":"Running","defaultHostName":"contoso-records-uat-proj-12.azurewebsites.net"}}""")
            .On(r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == slot + "/sourcecontrols/web", "{}");

        var result = await CallAsync("prepare_feature_slot", new() { ["repo"] = "recordscore", ["branch"] = "feature/PROJ-12", ["itemId"] = itemId });

        Assert.Equal("https://contoso-records-uat-proj-12.azurewebsites.net", result.GetProperty("url").GetString());
        var create = Assert.Single(_http.Requests, r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.AbsolutePath == slot);
        Assert.Contains("\"sourceWebAppId\":\"" + Sites + "contoso-records-uat\"", create.Body);
        var source = Assert.Single(_http.Requests, r => r.Request.Method == HttpMethod.Put && r.Request.RequestUri!.AbsolutePath.EndsWith("/sourcecontrols/web"));
        using var body = JsonDocument.Parse(source.Body!);
        Assert.Equal("feature/PROJ-12", body.RootElement.GetProperty("properties").GetProperty("branch").GetString());
        Assert.Equal("https://bitbucket.org/acmedev/recordscore", body.RootElement.GetProperty("properties").GetProperty("repoUrl").GetString());

        var refused = await _client.CallToolAsync("prepare_feature_slot", new Dictionary<string, object?> { ["repo"] = "recordscore", ["branch"] = "master" });
        Assert.True(refused.IsError);
    }

    [Theory]
    [InlineData("feature/SUP-1580", "shop-site-dev", "sup-1580")]
    [InlineData("feature/ui_grid upgrade", "shop-site-dev", "ui-grid-upgrade")]
    [InlineData("deploys/SUP-1561", "shop-site-uat", "sup-1561")]
    public void Slot_names_come_from_the_branch(string branch, string app, string slot) =>
        Assert.Equal(slot, ShippingTools.SlotNameFor(branch, app));

    [Theory]
    [InlineData("", false)]
    [InlineData("production", true)]
    public async Task A_production_slot_is_gated_by_its_swap_unless_it_swaps_automatically(string autoSwap, bool blocksMaster)
    {
        _http
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("Microsoft.ResourceGraph"), $$$"""
                {"data":[
                  {"id":"{{{Sites}}}contoso-records-dev","name":"contoso-records-dev","type":"microsoft.web/sites","host":"contoso-records-dev.azurewebsites.net"},
                  {"id":"{{{Sites}}}contoso-records-prod/slots/staging","name":"contoso-records-prod/staging","type":"microsoft.web/sites/slots","host":"contoso-records-prod-staging.azurewebsites.net"}
                ]}
                """)
            .OnUrl("contoso-records-dev/sourcecontrols/web", """{"properties":{"repoUrl":"https://bitbucket.org/acmedev/recordscore","branch":"master"}}""")
            .OnUrl("slots/staging/sourcecontrols/web", """{"properties":{"repoUrl":"https://bitbucket.org/acmedev/recordscore","branch":"master"}}""")
            .OnUrl("slots/staging/config/web", $$$"""{"properties":{"autoSwapSlotName":"{{{autoSwap}}}"}}""");

        var map = await CallAsync("find_deployments", new() { ["repo"] = "recordscore" });

        var slot = map.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("app").GetString() == "contoso-records-prod/staging");
        Assert.Equal("prod", slot.GetProperty("environment").GetString());
        Assert.Equal(!blocksMaster, slot.GetProperty("behindApproval").GetBoolean());
        Assert.Equal(blocksMaster, map.GetProperty("productionBranches").EnumerateArray().Any(b => b.GetString() == "master"));
        Assert.False(map.TryGetProperty("uatTarget", out var uat) && uat.ValueKind != JsonValueKind.Null);
    }

    [Theory]
    [InlineData("contoso-records-uat", "uat")]
    [InlineData("cmtn-website-qa", "qa")]
    [InlineData("ccr-ticketingadmin-dev", "dev")]
    [InlineData("datamanager-prod", "prod")]
    [InlineData("websitestage-core", "uat")]
    [InlineData("sl-website-15-dev", "dev")]
    [InlineData("doomdocs", "unknown")]
    public void Classifies_environments_from_app_names(string app, string environment) =>
        Assert.Equal(environment, DeploymentFinder.Classify(app, null));

    private void UseFeatureSlots() =>
        _host.Services.GetRequiredService<UserSettingsStore>().Update(s => s.Deployments.Repos.Add(
            new RepoDeploymentRule { Repo = "recordscore", ProductionBranches = ["master"], FeatureSlotApp = "contoso-records-uat" }));

    private void FakeAzure()
    {
        _http
            .On(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("Microsoft.ResourceGraph"), $$$"""
                {"data":[
                  {"id":"{{{Sites}}}contoso-records-uat","name":"contoso-records-uat","type":"microsoft.web/sites","host":"contoso-records-uat.azurewebsites.net"},
                  {"id":"{{{Sites}}}contoso-records-prod","name":"contoso-records-prod","type":"microsoft.web/sites","host":"contoso-records-prod.azurewebsites.net"},
                  {"id":"{{{Sites}}}records-legacy","name":"records-legacy","type":"microsoft.web/sites","host":"records-legacy.azurewebsites.net"},
                  {"id":"{{{Sites}}}other-app","name":"other-app","type":"microsoft.web/sites","host":"other-app.azurewebsites.net"}
                ]}
                """)
            .OnUrl("contoso-records-uat/sourcecontrols/web", """{"properties":{"repoUrl":"https://bitbucket.org/acmedev/recordscore","branch":"uat"}}""")
            .OnUrl("contoso-records-prod/sourcecontrols/web", """{"properties":{"repoUrl":"https://bitbucket.org/acmedev/recordscore","branch":"prod"}}""")
            .OnUrl("other-app/sourcecontrols/web", """{"properties":{"repoUrl":"https://bitbucket.org/acmedev/recordscore","branch":"kiosk-preview"}}""")
            .OnUrl("records-legacy/sourcecontrols/web", """{"properties":{"repoUrl":"https://bitbucket.org/acmedev/something-else","branch":"master"}}""")
            .OnUrl("contoso-records-uat/deployments", $$$"""
                {"value":[{"name":"contoso-records-uat/{{{MergeCommit}}}","properties":{"status":4,"active":true,"start_time":"2026-10-27T20:07:00Z","end_time":"2026-10-27T20:08:37Z","deployer":"Bitbucket","message":"Merged"}}]}
                """)
            .On(r => r.RequestUri!.Host == "contoso-records-uat.azurewebsites.net", "<html></html>");
    }

    private void FakePull(int id, string destination, string checkState = "SUCCESSFUL")
    {
        var path = $"repositories/acmedev/recordscore/pullrequests/{id}";
        _http
            .OnUrl(path, $$$"""
                {"id":{{{id}}},"title":"Fix","state":"OPEN","source":{"branch":{"name":"assistant/40-fix"},"commit":{"hash":"0123456789ab"}},
                 "destination":{"branch":{"name":"{{{destination}}}"}},"participants":[],"author":{"display_name":"Sam Rivera"},
                 "links":{"html":{"href":"https://bitbucket.org/acmedev/recordscore/pull-requests/{{{id}}}"}} }
                """)
            .OnUrl($"{path}/statuses", $$$"""{"values":[{"name":"build","state":"{{{checkState}}}"}]}""");
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> args)
    {
        var result = await _client.CallToolAsync(tool, args);
        Assert.False(result.IsError, Text(result));
        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    private static string Text(CallToolResult result) => string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private Task<int> AddItemAsync(string url) => _host.WithDbAsync(async db =>
    {
        var item = new Item { Title = "Work", Source = "jira", SourceUrl = url, Trust = SenderTrust.Organization, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Items.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    });

    private sealed class NoGitHubCli : IGitHubCliToken
    {
        public bool IsAvailable => false;

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FakeAzureCli : IAzureCliToken
    {
        public bool IsAvailable => true;

        public Task<string> GetTokenAsync(string tenantId, CancellationToken cancellationToken) => Task.FromResult("ado");

        public Task<string> GetTokenAsync(string tenantId, string resource, CancellationToken cancellationToken) => Task.FromResult("arm");

        public Task<IReadOnlyList<string>> ListTenantsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["t1"]);
    }

    private sealed class FakeTokens : IMicrosoftTokenProvider
    {
        public bool IsConfigured => true;

        public Task<string> GetAccessTokenAsync(IEnumerable<string> scopes, CancellationToken cancellationToken = default) => Task.FromResult("ms-token");
    }
}
