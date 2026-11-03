using System.Diagnostics;
using System.Text;
using Assistant.Agent.Triage;
using Assistant.Core.Signals;

namespace Assistant.Eval;

public sealed record EvalResult(EvalCase Case, string? Decision, string? Reasoning, string? Error, TimeSpan Elapsed)
{
    public bool Passed => Decision is not null && Case.Expected.Contains(Decision, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Runs each case through the same triage call the app makes, one at a time.</summary>
public sealed class EvalRunner(TriageService triage)
{
    public async Task<List<EvalResult>> RunAsync(IReadOnlyList<EvalCase> cases, Action<EvalResult> progress, CancellationToken cancellationToken)
    {
        var results = new List<EvalResult>();
        var now = DateTime.UtcNow;
        foreach (var c in cases)
        {
            var started = Stopwatch.StartNew();
            EvalResult result;
            try
            {
                var previous = c.PreviousDecision is null ? null : new TriageDecision
                {
                    Source = "eval",
                    Key = c.Name,
                    Outcome = TriageService.ParseDecision(c.PreviousDecision),
                    Title = c.PreviousTitle,
                };

                var judgment = await triage.JudgeThreadAsync(
                    [.. c.Messages.Select(m => m.ToThreadMessage(now))],
                    previous,
                    ["the user (\"Sent by the user\" messages)"],
                    calendarSummary: null,
                    c.Medium,
                    cancellationToken,
                    c.Elsewhere is null ? null : [.. c.Elsewhere.Select(m => m.ToThreadMessage(now))]);

                result = new EvalResult(c, Normalize(judgment.Decision), judgment.Reasoning, null, started.Elapsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new EvalResult(c, null, null, ex.Message, started.Elapsed);
            }

            results.Add(result);
            progress(result);
        }

        return results;
    }

    private static string Normalize(string decision) => decision.Trim().ToLowerInvariant().Replace('-', '_');

    public static string Report(IReadOnlyList<EvalResult> results, string model)
    {
        var passed = results.Count(r => r.Passed);
        var text = new StringBuilder();
        text.AppendLine("# Triage eval").AppendLine();
        text.AppendLine($"Model: {model}  ");
        text.AppendLine($"Run: {DateTime.Now:yyyy-MM-dd HH:mm}  ");
        text.AppendLine($"Score: {passed} of {results.Count} ({Percent(passed, results.Count)})").AppendLine();

        text.AppendLine("| Expected | Right | Wrong |").AppendLine("|---|---|---|");
        foreach (var g in results.GroupBy(r => string.Join(" or ", r.Case.Expected.Order(StringComparer.Ordinal))).OrderBy(g => g.Key))
        {
            text.AppendLine($"| {g.Key} | {g.Count(r => r.Passed)} | {g.Count(r => !r.Passed)} |");
        }

        var failures = results.Where(r => !r.Passed).ToList();
        if (failures.Count > 0)
        {
            text.AppendLine().AppendLine("## Misses").AppendLine();
            foreach (var r in failures)
            {
                text.AppendLine($"- **{r.Case.Name}**: expected {string.Join(" or ", r.Case.Expected)}, got {r.Decision ?? "an error"}.");
                if (r.Error is not null) text.AppendLine($"  Error: {r.Error}");
                if (r.Reasoning is not null) text.AppendLine($"  Model's reason: {r.Reasoning}");
                if (r.Case.Why is not null) text.AppendLine($"  Why it should be {r.Case.Expected[0]}: {r.Case.Why}");
            }
        }

        return text.ToString();
    }

    public static string Percent(int part, int whole) => whole == 0 ? "n/a" : $"{100.0 * part / whole:0}%";
}
