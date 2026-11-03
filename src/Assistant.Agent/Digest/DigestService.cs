using System.ComponentModel;
using System.Globalization;
using Assistant.Core.Notifications;
using Assistant.Core.Settings;
using Assistant.Core.Signals;
using Assistant.Core.Tools;
using Assistant.Data;
using Assistant.Tools.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Agent.Digest;

/// <summary>
/// Builds the digest at your chosen time each (week)day and tells you it's ready. If the app starts after that
/// time and you haven't had today's digest, it shows it then.
/// </summary>
public sealed class DigestService(
    DigestBuilder builder,
    IServiceScopeFactory scopes,
    INotifier notifier,
    UserSettingsStore settings,
    TimeProvider time,
    ILogger<DigestService> logger) : BackgroundService
{
    private const string LastShownKey = "digest:last-shown";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the connectors' first sync land before the first digest of the day.
        await Task.Delay(TimeSpan.FromSeconds(60), time, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Digest check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var digestSettings = settings.Current.Digest;
        var now = time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);

        if (!IsDue(digestSettings, now))
        {
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AssistantDbContext>();
        var shown = await db.ConnectorStates.FindAsync([LastShownKey], cancellationToken);
        if (shown?.Value == today.ToString("O"))
        {
            return false;
        }

        var digest = await builder.GetAsync(today, refresh: true, cancellationToken);
        notifier.Notify("Your day", digest.Headline, NotificationKind.Digest);

        if (shown is null)
        {
            db.ConnectorStates.Add(new ConnectorState { Key = LastShownKey, Value = today.ToString("O"), UpdatedAt = time.GetUtcNow().UtcDateTime });
        }
        else
        {
            shown.Value = today.ToString("O");
            shown.UpdatedAt = time.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Digest shown: {Headline}", digest.Headline);
        return true;
    }

    public static bool IsDue(DigestSettings digestSettings, DateTimeOffset localNow)
    {
        if (!digestSettings.Enabled)
        {
            return false;
        }

        if (digestSettings.WeekdaysOnly && localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return false;
        }

        var at = TimeOnly.TryParse(digestSettings.Time, CultureInfo.InvariantCulture, out var parsed) ? parsed : new TimeOnly(8, 0);
        return TimeOnly.FromDateTime(localNow.DateTime) >= at;
    }
}

/// <summary>The digest for MCP clients and the chat.</summary>
public sealed class DigestTools(DigestBuilder builder, TimeProvider time)
{
    [AssistantTool("get_digest", ToolCategory.Read, Title = "Get the daily digest")]
    [Description("The user's daily digest: today's meetings, what needs prep, what's due today, what's overdue, what's new, " +
        "waiting-on items gone quiet, and an AI-written overview when enabled. Built once a day and cached.")]
    public Task<DailyDigest> GetDigestAsync(
        [Description("Date as yyyy-MM-dd. Defaults to today.")] string? date = null,
        [Description("Rebuild instead of using the cached copy.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var day = date is null
            ? DateOnly.FromDateTime(time.GetLocalNow().DateTime)
            : DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d
                : throw new ToolException("date must be yyyy-MM-dd.");

        return builder.GetAsync(day, refresh, cancellationToken);
    }
}
