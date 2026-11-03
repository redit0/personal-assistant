using Assistant.Connectors.Microsoft;

namespace Assistant.Agent.Calendar;

public sealed record TimeSlot(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;
}

/// <summary>Finds open time between calendar events, optionally only within working hours.</summary>
public static class FreeTime
{
    public static IReadOnlyList<TimeSlot> Find(
        IEnumerable<GraphEvent> events,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan minimum,
        WorkingHours? workingHours,
        int maxSlots = 10)
    {
        var zone = workingHours?.Zone ?? TimeZoneInfo.Local;

        // Busy blocks, merged. Events marked free don't count; tentative ones do, to be safe.
        var busy = events
            .Where(e => !e.IsCancelled && !string.Equals(e.ShowAs, "free", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(e.ResponseStatus, "declined", StringComparison.OrdinalIgnoreCase))
            .Select(e => (Start: e.Start, End: e.End))
            .Where(b => b.End > fromUtc && b.Start < toUtc)
            .OrderBy(b => b.Start)
            .ToList();

        var merged = new List<(DateTime Start, DateTime End)>();
        foreach (var block in busy)
        {
            if (merged.Count > 0 && block.Start <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, block.End > merged[^1].End ? block.End : merged[^1].End);
            }
            else
            {
                merged.Add(block);
            }
        }

        var slots = new List<TimeSlot>();
        foreach (var (windowStart, windowEnd) in Windows(fromUtc, toUtc, workingHours, zone))
        {
            var cursor = windowStart;
            foreach (var block in merged.Where(b => b.End > windowStart && b.Start < windowEnd))
            {
                Add(cursor, block.Start < windowEnd ? block.Start : windowEnd);
                if (block.End > cursor)
                {
                    cursor = block.End;
                }
            }

            Add(cursor, windowEnd);
            if (slots.Count >= maxSlots)
            {
                break;
            }
        }

        return [.. slots.Take(maxSlots)];

        void Add(DateTime start, DateTime end)
        {
            if (end - start >= minimum)
            {
                slots.Add(new TimeSlot(
                    new DateTimeOffset(TimeZoneInfo.ConvertTimeFromUtc(start, zone), zone.GetUtcOffset(start)),
                    new DateTimeOffset(TimeZoneInfo.ConvertTimeFromUtc(end, zone), zone.GetUtcOffset(end))));
            }
        }
    }

    /// <summary>The searchable windows in UTC: the whole range, or each working day's hours within it.</summary>
    private static IEnumerable<(DateTime Start, DateTime End)> Windows(DateTime fromUtc, DateTime toUtc, WorkingHours? hours, TimeZoneInfo zone)
    {
        if (hours is null)
        {
            yield return (fromUtc, toUtc);
            yield break;
        }

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone));
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, zone));
        for (; day <= lastDay; day = day.AddDays(1))
        {
            if (hours.Days.Count > 0 && !hours.Days.Contains(day.DayOfWeek))
            {
                continue;
            }

            var start = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(hours.Start), zone);
            var end = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(hours.End), zone);
            if (start < fromUtc) start = fromUtc;
            if (end > toUtc) end = toUtc;
            if (end > start)
            {
                yield return (start, end);
            }
        }
    }
}
