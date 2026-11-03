namespace Assistant.Core.Notifications;

/// <summary>Shows a notification to you. The tray app implements this with Windows notifications.</summary>
public interface INotifier
{
    void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, int? itemId = null);
}

public enum NotificationKind
{
    Info,
    Reminder,
    Warning,
    Error,

    /// <summary>The daily digest is ready. Clicking opens the Today page.</summary>
    Digest,
}

/// <summary>Drops notifications. Used in tests and when no UI is attached.</summary>
public sealed class NullNotifier : INotifier
{
    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, int? itemId = null)
    {
    }
}
