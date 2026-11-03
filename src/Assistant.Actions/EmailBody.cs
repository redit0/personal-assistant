using System.Net;
using System.Text.RegularExpressions;

namespace Assistant.Actions;

/// <summary>Turns the plain text of a reply into what Outlook needs: a sign-off, and HTML that keeps its line breaks.</summary>
public static partial class EmailBody
{
    /// <summary>Adds the sign-off after a blank line, unless the text already ends with a closing of its own.</summary>
    public static string WithSignOff(string text, string signOff)
    {
        var body = Tidy(text);
        return EndsWithClosing(body) || body.EndsWith(Tidy(signOff), StringComparison.OrdinalIgnoreCase)
            ? body
            : $"{body}\n\n{Tidy(signOff)}";
    }

    /// <summary>Outlook collapses plain line breaks in a reply, so each one becomes a &lt;br&gt;.</summary>
    public static string ToHtml(string text) =>
        string.Join("<br>", Tidy(text).Split('\n').Select(WebUtility.HtmlEncode));

    /// <summary>The default sign-off, when you haven't written your own.</summary>
    public static string DefaultSignOff(string? displayName) =>
        displayName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } first ? $"Thanks,\n{first}" : "Thanks,";

    private static string Tidy(string text) =>
        ExtraBlankLines().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), "\n\n").Trim();

    /// <summary>A closing is the last line, or the line before a name.</summary>
    private static bool EndsWithClosing(string body)
    {
        var lines = body.Split('\n');
        return lines.TakeLast(2).Any(line => Closing().IsMatch(line.Trim()));
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExtraBlankLines();

    [GeneratedRegex(@"^(many |best |kind |warm |warmest )?(thanks|thank you|regards|wishes)( so much)?[,!.]?$|^(best|cheers|sincerely|yours truly|talk soon)[,!.]?$", RegexOptions.IgnoreCase)]
    private static partial Regex Closing();
}
