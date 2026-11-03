using Assistant.Agent.Triage;
using Assistant.Core.Settings;
using Assistant.Eval;
using Microsoft.Extensions.DependencyInjection;
using static Assistant.Mcp.Tests.Connectors.OutlookTriageTests;

namespace Assistant.Mcp.Tests;

/// <summary>The eval harness itself, with a scripted model. The real run uses your AI provider: assistant-eval.</summary>
public sealed class EvalTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsFile = Path.Combine(Path.GetTempPath(), $"pa-settings-{Guid.NewGuid():N}.json");
    private McpTestHost _host = null!;

    public async Task InitializeAsync() => _host = await McpTestHost.StartAsync();

    public async Task DisposeAsync() => await _host.DisposeAsync();

    public void Dispose() => File.Delete(_settingsFile);

    [Fact]
    public void Shipped_cases_load_and_cover_every_decision()
    {
        var cases = EvalCases.Load(CasesFolder());

        Assert.True(cases.Count >= 15);
        Assert.Equal(cases.Count, cases.Select(c => c.Name).Distinct().Count());
        foreach (var decision in new[] { "ask", "commitment", "waiting_on", "resolved", "fyi", "ignore" })
        {
            Assert.Contains(cases, c => c.Expected.Contains(decision));
        }

        Assert.Contains(cases, c => c.Elsewhere is { Count: > 0 });
    }

    [Fact]
    public async Task Runner_scores_cases_and_reports_the_misses()
    {
        var ai = new ScriptedChatClient
        {
            Respond = p => p.Contains("went live")
                ? """{"decision":"resolved","confidence":0.9}"""
                : """{"decision":"ask","confidence":0.9,"reasoning":"Looks like a request."}""",
        };
        var triage = new TriageService(ai, new UserSettingsStore(_settingsFile), _host.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var cases = new List<EvalCase>
        {
            new() { Name = "answered elsewhere", Expected = ["resolved"], PreviousDecision = "ask", PreviousTitle = "Tell Pat",
                Messages = [new() { From = "Pat", Body = "is it live?" }],
                Elsewhere = [new() { From = "Me", FromMe = true, Ago = "-10m", Body = "It went live this morning." }] },
            new() { Name = "newsletter", Expected = ["fyi", "ignore"], Why = "Marketing.",
                Messages = [new() { From = "News", Ago = "-2d", Body = "This week's deals" }] },
        };

        var results = await new EvalRunner(triage).RunAsync(cases, _ => { }, default);

        Assert.True(results[0].Passed);
        Assert.False(results[1].Passed);
        Assert.Contains("Later messages the user sent outside this thread", ai.Prompts[0]);
        var report = EvalRunner.Report(results, "test-model");
        Assert.Contains("Score: 1 of 2 (50%)", report);
        Assert.Contains("**newsletter**: expected fyi or ignore, got ask.", report);
        Assert.Contains("Why it should be fyi: Marketing.", report);
    }

    private static string CasesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "evals", "triage");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException("evals/triage not found above the test output.");
    }
}
