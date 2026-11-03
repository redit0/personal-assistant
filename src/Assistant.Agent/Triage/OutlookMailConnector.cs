using Assistant.Connectors.Microsoft;
using Assistant.Core.Connectors;
using Assistant.Core.Items;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Core.Text;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent.Triage;

/// <summary>
/// Outlook mail. Each sync: pull new and changed messages from the chosen folders, drop the obvious
/// noise with rules, ask the triage model about threads that changed, then report every thread whose
/// latest judgment is an ask, a commitment, or something you're waiting on. Threads judged resolved drop
/// out of that list, so their items close.
/// </summary>
public sealed class OutlookMailConnector(
    GraphClient graph,
    TriageService triage,
    ThreadTriageEngine engine,
    IMicrosoftTokenProvider auth,
    UserSettingsStore settings,
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<OutlookMailConnector> logger) : IConnector
{
    public const string Scope = "mail";

    public static readonly ConversationSource Source = new("outlook", Scope, "email", "https://outlook.office.com/mail/");

    private IReadOnlyList<string>? _myAddresses;

    public string Id => "outlook";

    public string DisplayName => "Outlook mail";

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, settings.Current.Outlook.IntervalMinutes));

    public string? NotReadyReason()
    {
        var o = settings.Current.Outlook;
        if (!o.MailEnabled) return "Turned off.";
        if (!auth.IsConfigured) return "Set up Microsoft sign-in in Settings first.";
        return triage.UnavailableReason is { } ai ? $"Needs an AI provider for triage. {ai}" : null;
    }

    public async Task<ConnectorSnapshot> FetchAsync(CancellationToken cancellationToken)
    {
        var o = settings.Current.Outlook;
        var warnings = new List<string>();
        _myAddresses ??= (await graph.GetMeAsync(cancellationToken)).Addresses;

        var folders = new List<(string Id, string Kind)>();
        if (o.IncludeInbox) folders.Add(("inbox", "inbox"));
        if (o.IncludeSent) folders.Add(("sentitems", "sent"));
        folders.AddRange(o.ExtraFolders.Select(f => (f.Id, f.Name)));

        foreach (var (folderId, kind) in folders)
        {
            await PullFolderAsync(folderId, kind, o.BackfillDays, cancellationToken);
        }

        var run = await engine.TriageChangedThreadsAsync(Source, o.MaxThreadsPerSync, _myAddresses, cancellationToken);
        warnings.AddRange(run.Warnings());
        logger.LogInformation("Outlook mail: triaged {Count} threads, {Backlog} waiting", run.Triaged, run.Backlog);
        await engine.PruneAsync(Source.SignalSource, cancellationToken);

        return new ConnectorSnapshot(await engine.BuildItemsAsync(Source, cancellationToken), new HashSet<string>(), warnings);
    }

    private async Task PullFolderAsync(string folderId, string kind, int backfillDays, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();

        var stateKey = $"outlook:delta:{folderId}";
        var state = await db.ConnectorStates.FindAsync([stateKey], cancellationToken);
        var since = time.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, backfillDays));

        var (messages, deltaLink) = await graph.GetMessageDeltaAsync(folderId, state?.Value, since, cancellationToken);
        var muted = settings.Current.Triage.MutedSenders;
        var ids = messages.Select(m => m.Id).ToList();
        var existing = await db.Signals.Where(s => s.Source == Id && ids.Contains(s.ExternalId)).ToDictionaryAsync(s => s.ExternalId, cancellationToken);

        foreach (var m in messages)
        {
            if (m.Removed)
            {
                continue; // Deleted or moved. Keep what triage already saw; the thread may live on elsewhere.
            }

            var fromMe = kind == "sent" || _myAddresses!.Contains(m.From?.Address ?? "", StringComparer.OrdinalIgnoreCase);
            // Document mentions and shares come from no-reply senders but are often real requests, so they skip the rules.
            var documentNotice = MailFilter.IsDocumentNotification(m);
            var filtered = fromMe || documentNotice ? null : MailFilter.Check(m, muted);
            if (filtered is null && !fromMe && !documentNotice && !existing.ContainsKey(m.Id))
            {
                filtered = MailFilter.CheckHeaders(await graph.GetHeadersAsync(m.Id, cancellationToken));
            }

            if (!existing.TryGetValue(m.Id, out var signal))
            {
                signal = new Signal { Source = Id, ExternalId = m.Id };
                db.Signals.Add(signal);
                existing[m.Id] = signal;
            }
            else if (signal.FilteredReason is not null)
            {
                filtered = signal.FilteredReason; // Keep the original header verdict.
            }

            signal.ThreadKey = m.ConversationId ?? m.Id;
            signal.Kind = kind;
            signal.Author = m.From?.ToString();
            signal.AuthorAddress = m.From?.Address;
            signal.Recipients = string.Join("; ", m.Recipients);
            signal.FromMe = fromMe;
            // A document notice is sent by Microsoft on behalf of whoever shared or commented, who may be outside your
            // company. Treat the sender as unknown so actions on these items always ask first.
            signal.AuthorTrust = fromMe ? SenderTrust.Self : documentNotice ? SenderTrust.Unknown : TrustOf(m.From?.Address);
            signal.Subject = m.Subject;
            signal.Body = SafeLinks.Unwrap(m.Body);
            signal.Url = m.WebLink;
            signal.ReceivedAt = m.ReceivedAt;
            signal.FilteredReason = filtered;
        }

        if (state is null)
        {
            db.ConnectorStates.Add(new ConnectorState { Key = stateKey, Value = deltaLink, UpdatedAt = time.GetUtcNow().UtcDateTime });
        }
        else
        {
            state.Value = deltaLink;
            state.UpdatedAt = time.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Your own domain counts as your organization; anyone else is external until proven otherwise.</summary>
    private SenderTrust TrustOf(string? address)
    {
        if (address is null)
        {
            return SenderTrust.Unknown;
        }

        var domain = address[(address.IndexOf('@') + 1)..];
        return _myAddresses!.Any(a => a.EndsWith("@" + domain, StringComparison.OrdinalIgnoreCase))
            ? SenderTrust.Organization
            : SenderTrust.External;
    }
}
