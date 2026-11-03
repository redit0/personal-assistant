using System.Globalization;
using System.Text.Json;
using Assistant.Agent;
using Assistant.Agent.Triage;
using Assistant.Core;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Assistant.Data;
using Assistant.Eval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// Runs the triage eval with the AI provider and model set in the app.
//   assistant-eval [cases.json or folder] [--min 0.85] [--report report.md]
//   assistant-eval --export-feedback [out.json]
var options = Options.Parse(args);
if (options.Help)
{
    Console.WriteLine("""
        Usage:
          assistant-eval [cases]               Run cases (a .json file or a folder; default: evals/triage).
              --min <0..1>                     Exit with code 1 when the score is below this (default 0.85).
              --report <file.md>               Also write the report to a Markdown file.
          assistant-eval --export-feedback [file]
                                               Turn your thumbs up and down into cases. They contain your
                                               messages, so they're saved under your data folder by default.
        """);
    return 0;
}

if (options.ExportFeedback)
{
    var output = options.Output ?? Path.Combine(AppPaths.DataDirectory, "evals", "my-feedback.json");
    await using var db = new AssistantDbContext(new DbContextOptionsBuilder<AssistantDbContext>()
        .UseSqlite($"Data Source={AppPaths.DatabaseFile};Mode=ReadOnly").Options);
    var exported = await FeedbackExport.ExportAsync(db, CancellationToken.None);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(exported, EvalCases.Json));
    Console.WriteLine($"Wrote {exported.Count} cases to {output}");
    Console.WriteLine($"Run them with: assistant-eval \"{output}\"");
    return 0;
}

var casesPath = options.Cases ?? FindDefaultCases();
var cases = EvalCases.Load(casesPath);
if (cases.Count == 0)
{
    Console.Error.WriteLine($"No cases found in {casesPath}.");
    return 2;
}

// The app's settings and keys, with an empty database so your own feedback doesn't steer the results.
var dbFile = Path.Combine(Path.GetTempPath(), $"assistant-eval-{Guid.NewGuid():N}.db");
var services = new ServiceCollection()
    .AddSingleton(new UserSettingsStore(AppPaths.SettingsFile))
    .AddSingleton<ISecretStore, WindowsCredentialSecretStore>()
    .AddSingleton(NullLoggerFactory.Instance)
    .AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance)
    .AddSingleton(TimeProvider.System)
    .AddSingleton<ChatClientFactory>()
    .AddSingleton<ITriageClientSource>(sp => sp.GetRequiredService<ChatClientFactory>())
    .AddSingleton<TriageService>()
    .AddAssistantData(dbFile)
    .BuildServiceProvider();

try
{
    await services.MigrateAssistantDatabaseAsync();
    var triage = services.GetRequiredService<TriageService>();
    if (triage.UnavailableReason is { } reason)
    {
        Console.Error.WriteLine($"No AI provider: {reason}");
        return 2;
    }

    Console.WriteLine($"Running {cases.Count} cases with {triage.ModelName}. Each one is a billed model call.");
    var results = await new EvalRunner(triage).RunAsync(cases, r =>
        Console.WriteLine($"  {(r.Passed ? "pass" : "MISS")}  {r.Case.Name} -> {r.Decision ?? r.Error} ({r.Elapsed.TotalSeconds:0.0}s)"),
        CancellationToken.None);

    var report = EvalRunner.Report(results, triage.ModelName);
    Console.WriteLine();
    Console.WriteLine(report);
    if (options.Report is not null)
    {
        await File.WriteAllTextAsync(options.Report, report);
    }

    var score = (double)results.Count(r => r.Passed) / results.Count;
    return score >= options.Min ? 0 : 1;
}
finally
{
    await services.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    File.Delete(dbFile);
}

static string FindDefaultCases()
{
    // Works from the repo root or from the build output inside it.
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "evals", "triage");
        if (Directory.Exists(candidate)) return candidate;
    }

    return Path.Combine(AppContext.BaseDirectory, "evals", "triage");
}

internal sealed record Options(string? Cases, double Min, string? Report, bool ExportFeedback, string? Output, bool Help)
{
    public static Options Parse(string[] args)
    {
        string? cases = null, report = null, output = null;
        var min = 0.85;
        bool export = false, help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--min": min = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--report": report = args[++i]; break;
                case "--export-feedback":
                    export = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) output = args[++i];
                    break;
                case "-h" or "--help": help = true; break;
                default: cases = args[i]; break;
            }
        }

        return new Options(cases, min, report, export, output, help);
    }
}
