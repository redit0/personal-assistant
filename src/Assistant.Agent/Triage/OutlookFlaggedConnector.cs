using Assistant.Connectors.Microsoft;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Text;

namespace Assistant.Agent.Triage;

/// <summary>
/// Messages you flag in Outlook become items, no AI involved. Clearing the flag or marking it complete
/// closes the item.
/// </summary>
public sealed class OutlookFlaggedConnector(GraphClient graph, IMicrosoftTokenProvider auth, UserSettingsStore settings) : IConnector
{
    public string Id => "outlook-flagged";

    public string DisplayName => "Flagged mail";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.Outlook.IntervalMinutes));

    public string? NotReadyReason()
    {
        if (!settings.Current.Outlook.FlaggedEnabled) return "Turned off.";
        return auth.IsConfigured ? null : "Set up Microsoft sign-in in Settings first.";
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var flagged = await graph.GetFlaggedAsync(cancellationToken);
        return ConnectorSnapshot.Complete([.. flagged.Select(m => new SourceItem(
            Scope: "flagged",
            ExternalId: $"flagged:{m.Id}",
            Title: $"Flagged: {m.Subject ?? "(no subject)"}",
            Type: ItemType.Task,
            Url: m.WebLink ?? "https://outlook.office.com/mail/",
            UpdatedAt: m.ReceivedAt,
            DueAt: m.FlagDueAt,
            Requester: m.From?.ToString(),
            Trust: SenderTrust.Self,
            Details: $"You flagged this message from {m.From?.Name ?? m.From?.Address ?? "someone"}.",
            SourceContent: Truncate(SafeLinks.Unwrap(m.Body))))]);
    }

    private static string? Truncate(string? text) => text is { Length: > 8000 } ? text[..8000] + "\n…" : text;
}
