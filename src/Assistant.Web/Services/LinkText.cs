using System.Text.RegularExpressions;

namespace Assistant.Web.Services;

/// <summary>Turns plain text into runs of text and links, for showing email and ticket text with clickable URLs.</summary>
public static partial class LinkText
{
    /// <summary>A run of text, with <see cref="Url"/> set when the run is a link.</summary>
    public readonly record struct Segment(string Text, string? Url);

    public static IEnumerable<Segment> Split(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        var position = 0;
        foreach (Match match in Url().Matches(text))
        {
            // Sentence punctuation after a link isn't part of it.
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '>', '\'', '"');
            if (match.Index > position)
            {
                yield return new Segment(text[position..match.Index], null);
            }

            yield return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? new Segment(url, url)
                : new Segment(url, null);
            position = match.Index + url.Length;
        }

        if (position < text.Length)
        {
            yield return new Segment(text[position..], null);
        }
    }

    /// <summary>
    /// Removes the untrusted_content label added for AI agents. In the app you can see where text came
    /// from, so the label is just noise.
    /// </summary>
    public static string? WithoutAgentLabel(string? text) =>
        text is null ? null : AgentLabel().Replace(text, "").Replace("&lt;/untrusted_content", "</untrusted_content").Trim();

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^<untrusted_content[^>]*>\n|\n</untrusted_content>$")]
    private static partial Regex AgentLabel();
}
