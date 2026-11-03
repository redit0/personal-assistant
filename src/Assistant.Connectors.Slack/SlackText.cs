using System.Net;
using System.Text.RegularExpressions;

namespace Assistant.Connectors.Slack;

/// <summary>Turns Slack's message markup into plain text: user and channel references, links, and escaped characters.</summary>
public static partial class SlackText
{
    public static string ToText(string text, Func<string, string?> userName)
    {
        var result = Reference().Replace(text, m =>
        {
            var target = m.Groups["target"].Value;
            var label = m.Groups["label"].Success ? m.Groups["label"].Value : null;
            return target[0] switch
            {
                '@' => "@" + (label ?? userName(target[1..]) ?? target[1..]),
                '#' => "#" + (label ?? target[1..]),
                '!' => "@" + (label ?? target[1..].Split('^')[0]),
                _ => label is null || label == target ? target : $"{label} ({target})",
            };
        });

        return WebUtility.HtmlDecode(result).Trim();
    }

    [GeneratedRegex(@"<(?<target>[^|>]+)(\|(?<label>[^>]*))?>")]
    private static partial Regex Reference();
}
