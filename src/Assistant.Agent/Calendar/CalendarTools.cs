using System.ComponentModel;
using Assistant.Connectors.Microsoft;
using Assistant.Core.Connectors;
using Assistant.Core.Tools;
using Assistant.Tools.Catalog;

namespace Assistant.Agent.Calendar;

public sealed record CalendarEvent(
    string? Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool AllDay,
    string? Location,
    string? Organizer,
    bool YouOrganize,
    string? YourResponse,
    string? ShowAs,
    string? Link);

/// <summary>Your Outlook calendar, for the chat and MCP clients. Needs the Outlook connection.</summary>
public sealed class CalendarTools(GraphClient graph, TimeProvider time)
{
    [AssistantTool("get_calendar", ToolCategory.Read, Title = "Get calendar")]
    [Description("Lists the user's calendar events in a time range (default: the rest of today). Times are in the user's local time zone.")]
    public async Task<IReadOnlyList<CalendarEvent>> GetCalendarAsync(
        [Description("Start of the range, ISO 8601 with offset. Defaults to now.")] DateTimeOffset? start = null,
        [Description("End of the range, ISO 8601 with offset. Defaults to the end of today. At most 31 days after start.")] DateTimeOffset? end = null,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = Range(start, end, TimeSpan.Zero);
        var events = await Run(() => graph.GetCalendarViewAsync(from, to, cancellationToken));
        return [.. events.Where(e => !e.IsCancelled).Select(e => new CalendarEvent(
            e.Subject, Local(e.Start), Local(e.End), e.IsAllDay, e.Location, e.Organizer, e.IsOrganizer, e.ResponseStatus, e.ShowAs, e.WebLink))];
    }

    [AssistantTool("find_free_time", ToolCategory.Read, Title = "Find free time")]
    [Description("Finds open slots of at least the given length in the user's calendar, by default within their Outlook working hours " +
        "over the next five days. Events marked free and declined meetings don't count as busy.")]
    public async Task<IReadOnlyList<TimeSlot>> FindFreeTimeAsync(
        [Description("Minimum slot length in minutes.")] int durationMinutes = 30,
        [Description("Start of the search, ISO 8601 with offset. Defaults to now.")] DateTimeOffset? start = null,
        [Description("End of the search, ISO 8601 with offset. Defaults to five days from now.")] DateTimeOffset? end = null,
        [Description("Only look within the user's Outlook working hours.")] bool withinWorkingHours = true,
        CancellationToken cancellationToken = default)
    {
        if (durationMinutes is < 5 or > 24 * 60)
        {
            throw new ToolException("durationMinutes must be between 5 and 1440.");
        }

        var (from, to) = Range(start, end, TimeSpan.FromDays(5));
        var events = await Run(() => graph.GetCalendarViewAsync(from, to, cancellationToken));
        var hours = withinWorkingHours ? await Run(() => graph.GetWorkingHoursAsync(cancellationToken)) : null;
        return FreeTime.Find(events, from, to, TimeSpan.FromMinutes(durationMinutes), hours);
    }

    private (DateTime From, DateTime To) Range(DateTimeOffset? start, DateTimeOffset? end, TimeSpan defaultLength)
    {
        var now = time.GetLocalNow();
        var from = (start ?? now).UtcDateTime;
        var to = end?.UtcDateTime ?? (defaultLength == TimeSpan.Zero
            ? new DateTimeOffset(now.Date.AddDays(1), now.Offset).UtcDateTime
            : from + defaultLength);

        if (to <= from)
        {
            throw new ToolException("end must be after start.");
        }

        return (from, to - from > TimeSpan.FromDays(31) ? from.AddDays(31) : to);
    }

    private static DateTimeOffset Local(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToLocalTime();

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (ConnectorException ex)
        {
            throw new ToolException(ex.Message);
        }
    }
}
