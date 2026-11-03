using System.Text.RegularExpressions;

namespace Assistant.Core.Text;

/// <summary>
/// Undoes Microsoft Defender "Safe Links" rewriting, which turns every link in Outlook and Teams into
/// https://xxx.safelinks.protection.outlook.com/?url=&lt;real link, URL-encoded&gt;&amp;data=... .
/// The real address is right there in the url parameter, so this just decodes it.
/// </summary>
public static partial class SafeLinks
{
    /// <summary>Replaces every Safe Links URL in the text with the link it wraps.</summary>
    public static string? Unwrap(string? text) =>
        string.IsNullOrEmpty(text) || !text.Contains("safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase)
            ? text
            : WrappedLink().Replace(text, m => TryUnwrap(m.Value) ?? m.Value);

    /// <summary>The link a single Safe Links URL wraps, or null if it isn't one or can't be decoded.</summary>
    public static string? TryUnwrap(string url)
    {
        // Plain-text and HTML bodies sometimes carry the separator as an entity.
        url = url.Replace("&amp;", "&", StringComparison.Ordinal);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith(".safelinks.protection.outlook.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!pair.StartsWith("url=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = Uri.UnescapeDataString(pair[4..].Replace('+', ' '));

            // Links get wrapped twice when mail is forwarded between protected tenants.
            if (TryUnwrap(target) is { } inner)
            {
                target = inner;
            }

            // Only hand back ordinary links; anything odd keeps the original wrapper.
            return Uri.TryCreate(target, UriKind.Absolute, out var real) && real.Scheme is "http" or "https" or "mailto"
                ? target
                : null;
        }

        return null;
    }

    [GeneratedRegex(@"https?://[A-Za-z0-9-]+\.safelinks\.protection\.outlook\.com/[^\s<>""'\])]+", RegexOptions.IgnoreCase)]
    private static partial Regex WrappedLink();
}
