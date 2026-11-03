using System.Windows;
using System.Windows.Interop;
using Assistant.Connectors.Microsoft;
using Assistant.Core;
using Assistant.Mcp;
using Assistant.Web.Services;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;
    private TaskbarIcon? _tray;
    private ShellWindow? _shell;

    public App(IServiceProvider services)
    {
        _services = services;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    public static new App Current => (App)Application.Current;

    public TaskbarIcon? Tray => _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _tray = (TaskbarIcon)FindResource("TrayIcon");

        // Efficiency mode would throttle the process, which also throttles the MCP server.
        _tray.ForceCreate(enablesEfficiencyMode: false);

        var status = _services.GetRequiredService<McpHostStatus>();
        _tray.ToolTipText = status.IsRunning
            ? $"Personal Assistant\nMCP: {status.Endpoint}"
            : "Personal Assistant\nMCP server not running";

        _services.GetRequiredService<MicrosoftAuthService>().WindowHandleProvider = () =>
            _shell is null ? IntPtr.Zero : new WindowInteropHelper(_shell).Handle;

        if (status.Error is not null)
        {
            _tray.ShowNotification("MCP server not running", status.Error, NotificationIcon.Warning);
        }

        // --page=connectors opens the window on that page. On first run, open Settings.
        var page = e.Args.FirstOrDefault(a => a.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))?["--page=".Length..];
        if (page is not null)
        {
            ShowPage(page);
        }
        else if (!System.IO.File.Exists(AppPaths.SettingsFile))
        {
            ShowPage("settings");
        }
    }

    public void ShowPage(string path)
    {
        if (_shell is null)
        {
            _shell = new ShellWindow(_services, path);
        }
        else
        {
            _services.GetRequiredService<NavigationBridge>().NavigateTo(path);
        }

        _shell.Show();
        if (_shell.WindowState == WindowState.Minimized)
        {
            _shell.WindowState = WindowState.Normal;
        }

        _shell.Activate();
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        if (_shell is { IsVisible: true, IsActive: true })
        {
            _shell.Hide();
        }
        else
        {
            ShowPage("");
        }
    }

    /// <summary>Where clicking the most recent notification goes.</summary>
    public string NotificationClickPath { get; set; } = "";

    private void OnBalloonClicked(object sender, RoutedEventArgs e) => ShowPage(NotificationClickPath);

    private void OnOpenToday(object sender, RoutedEventArgs e) => ShowPage("today");

    private void OnOpenItems(object sender, RoutedEventArgs e) => ShowPage("");

    private void OnOpenChat(object sender, RoutedEventArgs e) => ShowPage("chat");

    private void OnOpenActivity(object sender, RoutedEventArgs e) => ShowPage("activity");

    private void OnOpenSettings(object sender, RoutedEventArgs e) => ShowPage("settings");

    private void OnExit(object sender, RoutedEventArgs e)
    {
        _shell?.CloseForExit();
        _tray?.Dispose();
        Shutdown();
    }
}
