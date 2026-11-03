using System.Net;
using System.Text.RegularExpressions;

namespace Assistant.Core.Text;

/// <summary>Turns the HTML in chat messages and work items into readable plain text.</summary>
public static partial class HtmlText
{
    public static string? ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Mention().Replace(html, m => "@" + m.Groups["name"].Value);
        text = BlockEnd().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = BlankLines().Replace(text, "\n\n").Trim();
        return text.Length == 0 ? null : text;
    }

    [GeneratedRegex(@"<at\b[^>]*>(?<name>.*?)</at>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Mention();

    [GeneratedRegex(@"<\s*(br|/p|/div|/li|/h\d|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLines();
}
