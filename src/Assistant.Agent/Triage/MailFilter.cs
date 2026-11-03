using System.Text.RegularExpressions;
using Assistant.Connectors.Microsoft;

namespace Assistant.Agent.Triage;

/// <summary>
/// Cheap rules that drop mail nobody needs an AI to read: newsletters, automated senders, meeting
/// invites (the calendar handles those), and senders you muted. Runs in two steps so headers are only
/// fetched for messages that pass the first.
/// </summary>
public static partial class MailFilter
{
    /// <summary>Checks that need only the message itself. Returns why it was dropped, or null.</summary>
    public static string? Check(GraphMessage message, IReadOnlyCollection<string> mutedSenders)
    {
        if (message.IsMeetingMessage)
        {
            return "meeting invite";
        }

        var address = message.From?.Address ?? "";
        if (AutomatedSender().IsMatch(address))
        {
            return "automated sender";
        }

        if (IsMuted(address, mutedSenders))
        {
            return "muted sender";
        }

        // Outlook's Focused Inbox puts newsletters and notifications in "other".
        return string.Equals(message.InferenceClassification, "other", StringComparison.OrdinalIgnoreCase)
            ? "Focused Inbox: other"
            : null;
    }

    /// <summary>Checks on internet headers, for messages that passed <see cref="Check"/>.</summary>
    public static string? CheckHeaders(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.ContainsKey("List-Unsubscribe") || headers.ContainsKey("List-Id"))
        {
            return "mailing list";
        }

        if (headers.TryGetValue("Precedence", out var precedence) && precedence.Trim().ToLowerInvariant() is "bulk" or "list" or "junk")
        {
            return $"precedence: {precedence.Trim()}";
        }

        if (headers.TryGetValue("Auto-Submitted", out var auto) && !auto.Trim().Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return "auto-submitted";
        }

        return headers.ContainsKey("X-Auto-Response-Suppress") && headers.ContainsKey("X-MS-Exchange-Generated-Message-Source")
            ? "Exchange-generated"
            : null;
    }

    /// <summary>
    /// SharePoint and OneDrive notices that someone mentioned you in a document comment, replied to your comment,
    /// assigned you a task, or shared a file with you. They come from no-reply addresses, so they'd otherwise be
    /// dropped as automated mail, but they're often real requests.
    /// </summary>
    public static bool IsDocumentNotification(GraphMessage message)
    {
        var address = message.From?.Address ?? "";
        var domain = address[(address.IndexOf('@') + 1)..].ToLowerInvariant();
        var fromMicrosoftDocs = domain is "sharepointonline.com" or "onedrive.com" or "sharepoint.com"
            || domain.EndsWith(".sharepointonline.com", StringComparison.Ordinal)
            || domain.EndsWith(".sharepoint.com", StringComparison.Ordinal);

        return fromMicrosoftDocs && DocumentNotificationSubject().IsMatch(message.Subject ?? "");
    }

    [GeneratedRegex(@"\b(mentioned you|replied to (a|your) comment|replied to you|assigned (you )?a task|shared .+ with you|left a comment|commented on)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DocumentNotificationSubject();

    public static bool IsMuted(string address, IReadOnlyCollection<string> mutedSenders) =>
        mutedSenders.Any(m =>
            m.StartsWith('@')
                ? address.EndsWith(m, StringComparison.OrdinalIgnoreCase)
                : string.Equals(address, m, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"^(no[-_.]?reply|do[-_.]?not[-_.]?reply|notifications?|mailer-daemon|postmaster|bounce[s]?|alerts?)([-+._].*)?@", RegexOptions.IgnoreCase)]
    private static partial Regex AutomatedSender();
}
