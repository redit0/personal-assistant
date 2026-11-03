using System.Windows;
using Assistant.Core.Notifications;
using Assistant.Tools.Catalog;
using Assistant.Web.Services;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace Assistant.App;

/// <summary>Shows approval prompts one at a time, on the UI thread.</summary>
public sealed class WpfApprovalBroker(INotifier notifier, ILogger<WpfApprovalBroker> logger) : IApprovalBroker
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<bool> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return false;
        }

        await _oneAtATime.WaitAsync(cancellationToken);
        try
        {
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ApprovalWindow? window = null;

            await dispatcher.InvokeAsync(() =>
            {
                window = new ApprovalWindow(request, Timeout);
                window.Closed += (_, _) => decision.TrySetResult(window.Approved);
                window.Show(); // Not activated: see ApprovalWindow.
            });

            notifier.Notify("Approval needed", $"{request.Caller.DisplayName} wants to run {request.ToolName}.", NotificationKind.Warning);

            // If the client disconnects, close the prompt so it doesn't sit there.
            await using var registration = cancellationToken.Register(() => dispatcher.InvokeAsync(() => window?.Close()));

            var approved = await decision.Task;
            logger.LogInformation("{Caller} {Tool}: {Decision}", request.Caller.Id, request.ToolName, approved ? "approved" : "denied");
            return approved && !cancellationToken.IsCancellationRequested;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }
}

/// <summary>Shows notifications from the tray icon.</summary>
public sealed class TrayNotifier : INotifier
{
    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info, int? itemId = null)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // Clicking the notification opens the page it's about.
            App.Current.NotificationClickPath = kind == NotificationKind.Digest ? "today" : "";
            App.Current.Tray?.ShowNotification(title, message, kind switch
            {
                NotificationKind.Warning => NotificationIcon.Warning,
                NotificationKind.Error => NotificationIcon.Error,
                _ => NotificationIcon.Info,
            });
        });
    }
}

public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) =>
        Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
}
