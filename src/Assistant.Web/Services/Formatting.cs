using Assistant.Core.Items;

namespace Assistant.Web.Services;

public static class Formatting
{
    /// <summary>"in 3h", "tomorrow 9:00 AM", "2 days ago", and so on.</summary>
    public static string Relative(DateTime utc, DateTime nowUtc)
    {
        var local = utc.ToLocalTime();
        var now = nowUtc.ToLocalTime();
        var delta = utc - nowUtc;
        var abs = delta.Duration();

        if (abs < TimeSpan.FromMinutes(1)) return "now";
        if (abs < TimeSpan.FromHours(1)) return delta > TimeSpan.Zero ? $"in {abs.Minutes}m" : $"{abs.Minutes}m ago";
        if (local.Date == now.Date) return delta > TimeSpan.Zero ? $"today {local:t}" : $"{(int)abs.TotalHours}h ago";
        if (local.Date == now.Date.AddDays(1)) return $"tomorrow {local:t}";
        if (local.Date == now.Date.AddDays(-1)) return $"yesterday {local:t}";
        if (abs < TimeSpan.FromDays(7)) return delta > TimeSpan.Zero ? $"{local:dddd} {local:t}" : $"{(int)abs.TotalDays} days ago";
        return local.ToString(local.Year == now.Year ? "MMM d" : "MMM d, yyyy");
    }

    public static string TypeLabel(ItemType type) => type switch
    {
        ItemType.Ask => "Ask",
        ItemType.Commitment => "Commitment",
        ItemType.Review => "Review",
        ItemType.Assigned => "Assigned",
        ItemType.Event => "Event",
        ItemType.WaitingOn => "Waiting on",
        _ => "Task",
    };

    public static string PriorityClass(Priority priority) => priority switch
    {
        Priority.Urgent => "p-urgent",
        Priority.High => "p-high",
        Priority.Low => "p-low",
        _ => "p-normal",
    };
}
