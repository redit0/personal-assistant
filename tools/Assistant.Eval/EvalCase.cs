using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Agent.Triage;

namespace Assistant.Eval;

/// <summary>One labeled conversation: what triage should decide for it.</summary>
public sealed record EvalCase
{
    public required string Name { get; init; }

    /// <summary>"email", "Teams chat", or "Slack conversation", as triage describes it to the model.</summary>
    public string Medium { get; init; } = "email";

    /// <summary>Decisions that count as right, such as ["ask"] or ["fyi", "ignore"].</summary>
    public required List<string> Expected { get; init; }

    public required List<EvalMessage> Messages { get; init; }

    /// <summary>The user's later messages outside the thread, to test answers given elsewhere.</summary>
    public List<EvalMessage>? Elsewhere { get; init; }

    /// <summary>An earlier judgment, such as "ask", for cases that test whether it now stands or is resolved.</summary>
    public string? PreviousDecision { get; init; }

    public string? PreviousTitle { get; init; }

    /// <summary>Why the expected answer is right. Shown when the case fails.</summary>
    public string? Why { get; init; }
}

public sealed record EvalMessage
{
    public required string From { get; init; }

    public string To { get; init; } = "";

    public bool FromMe { get; init; }

    /// <summary>When it was sent, relative to now, such as "-2h", "-3d", or "-30m".</summary>
    public string Ago { get; init; } = "-1h";

    public string? Subject { get; init; }

    public required string Body { get; init; }

    public ThreadMessage ToThreadMessage(DateTime now) => new(From, To, now + ParseAgo(Ago), FromMe, Subject, Body);

    private static TimeSpan ParseAgo(string ago)
    {
        var value = double.Parse(ago[..^1], CultureInfo.InvariantCulture);
        return ago[^1] switch
        {
            'm' => TimeSpan.FromMinutes(value),
            'h' => TimeSpan.FromHours(value),
            'd' => TimeSpan.FromDays(value),
            _ => throw new FormatException($"Use m, h, or d in \"{ago}\"."),
        };
    }
}

public static class EvalCases
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every case in a .json file, or in all .json files in a folder.</summary>
    public static List<EvalCase> Load(string path)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.json").Order().ToArray() : [path];
        return [.. files.SelectMany(f => JsonSerializer.Deserialize<List<EvalCase>>(File.ReadAllText(f), Json) ?? [])];
    }
}
