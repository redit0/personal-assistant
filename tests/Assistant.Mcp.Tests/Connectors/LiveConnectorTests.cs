using Assistant.Connectors.GitHub;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace Assistant.Mcp.Tests.Connectors;

/// <summary>
/// Read-only checks against the real services, skipped unless PA_LIVE_TESTS=1. They use a temporary
/// settings file and never touch the app's database.
/// </summary>
public sealed class LiveConnectorTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("PA_LIVE_TESTS") == "1";

    [Fact]
    public async Task GitHub_with_the_cli_login()
    {
        if (!Enabled)
        {
            output.WriteLine("Skipped: set PA_LIVE_TESTS=1 to run.");
            return;
        }

        var settingsFile = Path.Combine(Path.GetTempPath(), $"pa-live-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new UserSettingsStore(settingsFile);
            settings.Update(s => s.GitHub.Enabled = true);
            var http = new HttpClientFactoryStub();
            var connector = new GitHubConnector(http, settings, new InMemorySecretStore(),
                new GitHubCliToken(NullLogger<GitHubCliToken>.Instance), TimeProvider.System, NullLogger<GitHubConnector>.Instance);

            var snapshot = await connector.FetchAsync(default);

            output.WriteLine($"{snapshot.Items.Count} items, failed scopes: {string.Join(",", snapshot.FailedScopes)}");
            foreach (var group in snapshot.Items.GroupBy(i => i.Type))
            {
                output.WriteLine($"{group.Key}: {group.Count()}");
            }
        }
        finally
        {
            File.Delete(settingsFile);
        }
    }

    [Fact]
    public async Task Azure_cli_returns_an_azure_devops_token()
    {
        var tenant = Environment.GetEnvironmentVariable("PA_LIVE_TENANT");
        if (!Enabled || string.IsNullOrEmpty(tenant))
        {
            output.WriteLine("Skipped: set PA_LIVE_TESTS=1 and PA_LIVE_TENANT to run.");
            return;
        }

        var cli = new Assistant.Connectors.Microsoft.AzureCliToken(NullLogger<Assistant.Connectors.Microsoft.AzureCliToken>.Instance);
        Assert.True(cli.IsAvailable);

        var token = await cli.GetTokenAsync(tenant, default);

        // A JWT has three dot-separated parts. Never print the token itself.
        Assert.Equal(3, token.Split('.').Length);
        output.WriteLine($"Got a token for {tenant} ({token.Length} characters).");
    }

    [Fact]
    public async Task Triage_with_the_configured_ai_provider()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("PA_LIVE_AI") != "1")
        {
            output.WriteLine("Skipped: set PA_LIVE_TESTS=1 and PA_LIVE_AI=1 to run. This spends a little on your AI provider.");
            return;
        }

        // Reads your real settings and key, but never saves anything.
        var settings = new UserSettingsStore(Assistant.Core.AppPaths.SettingsFile);
        await using var host = await McpTestHost.StartAsync();
        var factory = new Assistant.Agent.ChatClientFactory(settings, new WindowsCredentialSecretStore(), NullLoggerFactory.Instance);
        var triage = new Assistant.Agent.Triage.TriageService(factory, settings,
            host.Services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), TimeProvider.System);

        var now = DateTime.UtcNow;
        var ask = await triage.JudgeThreadAsync(
        [
            new("Dana Smith <dana@example.com>", "Sam Rivera <sam@example.com>", now.AddHours(-2), false, "Q4 numbers",
                "Hi Sam, can you send me the Q4 utilization numbers before Thursday's staffing review? Thanks, Dana"),
        ], null, ["sam@example.com"], "- Thu 10:00 AM: Staffing review", "email", default);

        var promo = await triage.JudgeThreadAsync(
        [
            new("Cloud Weekly <news@cloudweekly.example>", "sam@example.com", now.AddHours(-1), false, "5 tips for cutting cloud costs",
                "This week: five tips for cutting your cloud bill. Read more on our blog."),
        ], null, ["sam@example.com"], null, "email", default);

        output.WriteLine($"Model: {triage.ModelName}");
        output.WriteLine($"Ask thread   -> {ask.Decision}: \"{ask.Title}\" due {ask.Due} (confidence {ask.Confidence}) {ask.Reasoning}");
        output.WriteLine($"Promo thread -> {promo.Decision} (confidence {promo.Confidence}) {promo.Reasoning}");

        Assert.Equal(Core.Signals.TriageOutcome.Ask, Assistant.Agent.Triage.TriageService.ParseDecision(ask.Decision));
        Assert.NotNull(Assistant.Agent.Triage.TriageService.ParseDue(ask.Due));
        Assert.Contains(Assistant.Agent.Triage.TriageService.ParseDecision(promo.Decision), new[] { Core.Signals.TriageOutcome.Ignore, Core.Signals.TriageOutcome.Fyi });
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri("https://api.github.com/") };
    }
}
