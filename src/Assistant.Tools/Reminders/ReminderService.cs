using Assistant.Core.Items;
using Assistant.Core.Notifications;
using Assistant.Core.Settings;
using Assistant.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Reminders;

/// <summary>
/// Once a minute: brings snoozed items back and reminds you of items coming due.
/// This is the simple Phase 0 version; Phase 2 adds per-type lead times and working hours.
/// </summary>
public sealed class ReminderService(
    IServiceScopeFactory scopes,
    INotifier notifier,
    UserSettingsStore settings,
    TimeProvider time,
    ILogger<ReminderService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reminder check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var lead = TimeSpan.FromMinutes(Math.Max(0, settings.Current.Reminders.LeadTimeMinutes));

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();

        var waking = await db.Items
            .Where(i => i.Status == ItemStatus.Snoozed && i.SnoozedUntil != null && i.SnoozedUntil <= now)
            .ToListAsync(cancellationToken);

        foreach (var item in waking)
        {
            item.Status = ItemStatus.Open;
            item.SnoozedUntil = null;
            item.UpdatedAt = now;
            notifier.Notify("Back on your list", item.Title, NotificationKind.Reminder, item.Id);
        }

        // Reminders you asked for. If the item was closed in the meantime (you did it, or the person you were
        // waiting on replied), there's nothing to remind you about.
        var asked = await db.Items
            .Where(i => i.RemindAt != null && i.RemindAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var item in asked)
        {
            if (item.Status is ItemStatus.Open or ItemStatus.Snoozed)
            {
                notifier.Notify($"Reminder: {item.Title}", item.ReminderNote ?? "You asked to be reminded about this.", NotificationKind.Reminder, item.Id);
            }

            item.RemindAt = null;
            item.ReminderNote = null;
        }

        var cutoff = now + lead;
        var dueSoon = await db.Items
            .Where(i => i.Status == ItemStatus.Open && i.DueAt != null && i.DueAt <= cutoff)
            .Where(i => i.RemindedForDueAt == null || i.RemindedForDueAt != i.DueAt)
            .ToListAsync(cancellationToken);

        foreach (var item in dueSoon)
        {
            item.RemindedForDueAt = item.DueAt;
            var title = item.DueAt <= now ? "Overdue" : $"Due {item.DueAt!.Value.ToLocalTime():t}";
            notifier.Notify(title, item.Title, NotificationKind.Reminder, item.Id);
        }

        if (waking.Count > 0 || dueSoon.Count > 0 || asked.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
