using System.Text.RegularExpressions;

namespace Assistant.Tools.Catalog;

/// <summary>
/// Labels text that came from outside (emails, messages, tickets) before it reaches a model.
/// Anyone can send you an email, so its text is data to read, never instructions to follow.
/// </summary>
public static partial class UntrustedContent
{
    public const string Notice = Core.Agents.AgentGuidance.UntrustedContentNotice;

    public static string? Wrap(string source, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        // Stop the content from closing the tag early and smuggling text outside it.
        var neutralized = TagPattern().Replace(text, m => m.Value.Replace("<", "&lt;"));
        return $"<untrusted_content source=\"{source}\">\n{neutralized}\n</untrusted_content>";
    }

    [GeneratedRegex(@"</?\s*untrusted_content", RegexOptions.IgnoreCase)]
    private static partial Regex TagPattern();
}
