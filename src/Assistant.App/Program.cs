using System.Net;
using System.Windows;
using Assistant.Actions;
using Assistant.Agent;
using Assistant.Connectors.Atlassian;
using Assistant.Connectors.GitHub;
using Assistant.Connectors.Microsoft;
using Assistant.Core;
using Assistant.Core.Notifications;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Assistant.Data;
using Assistant.Mcp;
using Assistant.Tools;
using Assistant.Tools.Catalog;
using Assistant.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Assistant.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack runs its install, update, and uninstall hooks here and exits; otherwise it returns at once.
        // Installing turns on start at sign-in, and uninstalling removes it.
        Velopack.VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => WindowsStartup.Enable())
            .OnBeforeUninstallFastCallback(_ => WindowsStartup.Disable())
            .Run();

        AppPaths.EnsureCreated();

        // Started from inside a packaged app (for example the Claude desktop app)? Then Windows would
        // redirect our data to that app's private copy of AppData. Start again outside it instead.
        var redirected = LaunchContext.AppDataIsRedirected();
        if (redirected && LaunchContext.TryRelaunchOutsidePackage())
        {
            return 0;
        }

        using var singleInstance = new Mutex(initiallyOwned: true, @"Local\PersonalAssistant.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Personal Assistant is already running. Look for its icon in the system tray.",
                "Personal Assistant", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(System.IO.Path.Combine(AppPaths.LogDirectory, "app-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        try
        {
            var host = BuildHost(args, out var status);
            host.Services.MigrateAssistantDatabaseAsync().GetAwaiter().GetResult();

            // Create the MCP token now so it's ready to copy from Settings on first run.
            host.Services.GetRequiredService<McpTokenProvider>().GetToken();

            try
            {
                host.StartAsync().GetAwaiter().GetResult();
                status.IsRunning = status.Port > 0;
            }
            catch (System.IO.IOException ex)
            {
                // Usually the port is taken. The UI still works; the MCP server and reminders don't.
                status.IsRunning = false;
                status.Error = $"Could not listen on port {status.Port}: {ex.Message}. Pick another port in Settings and restart.";
                Log.Error(ex, "Host failed to start");
            }

            Log.Information("Started. MCP running: {Running} on {Endpoint}. Data folder: {Data}", status.IsRunning, status.Endpoint, AppPaths.DataDirectory);
            if (redirected)
            {
                Log.Warning("Windows is redirecting this app's AppData into another app's package, and relaunching through Explorer didn't help. Settings saved now may not appear when the app is started normally.");
            }

            var app = new App(host.Services);
            app.InitializeComponent();
            app.Run();

            host.StopAsync().Wait(TimeSpan.FromSeconds(5));
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Fatal startup error");
            MessageBox.Show($"Personal Assistant could not start:\n\n{ex.Message}\n\nDetails are in {AppPaths.LogDirectory}.",
                "Personal Assistant", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static WebApplication BuildHost(string[] args, out McpHostStatus status)
    {
        var settings = new UserSettingsStore(AppPaths.SettingsFile);
        var mcp = settings.Current.Mcp;
        status = new McpHostStatus { Port = mcp.Enabled ? mcp.Port : 0 };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddSerilog();
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Loopback only. Port 0 when MCP is off keeps Kestrel available for OAuth redirects later.
            k.Listen(IPAddress.Loopback, mcp.Enabled ? mcp.Port : 0);
        });

        var services = builder.Services;
        services.AddSingleton(settings);
        services.AddSingleton(status);
        services.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();
        services.AddSingleton<INotifier, TrayNotifier>();
        services.AddSingleton<IApprovalBroker, WpfApprovalBroker>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IStartupService, WindowsStartup>();
        services.AddSingleton<VelopackUpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<VelopackUpdateService>());
        services.AddHostedService(sp => sp.GetRequiredService<VelopackUpdateService>());
        services.AddAssistantData(AppPaths.DatabaseFile);
        services.AddAssistantTools();
        services.AddAssistantReminders();
        services.AddAssistantSync();
        services.AddGitHubConnector();
        services.AddAtlassianConnectors();
        services.AddMicrosoftConnectors();
        services.AddAssistantActions();
        services.AddAssistantMcp();
        services.AddAssistantAgent();
        services.AddAssistantWeb();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif

        var host = builder.Build();
        if (mcp.Enabled)
        {
            host.MapAssistantMcp();
        }

        return host;
    }
}
