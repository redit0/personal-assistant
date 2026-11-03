using System.Reflection;
using Assistant.Core.Notifications;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Assistant.Web.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Assistant.App;

/// <summary>
/// Updates from GitHub releases published by build/release.ps1. Checks once a day when turned on, downloads in
/// the background, and leaves the restart to you. Does nothing when running from a build folder.
/// </summary>
public sealed class VelopackUpdateService(
    UserSettingsStore settings,
    ISecretStore secrets,
    INotifier notifier,
    ILogger<VelopackUpdateService> logger) : BackgroundService, IUpdateService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager? _manager;
    private string? _managerKey;
    private UpdateInfo? _pending;

    public string CurrentVersion { get; private set; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";

    public UpdateState State { get; private set; } = UpdateState.Idle;

    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    public string? Error { get; private set; }

    public DateTime? LastCheckedAt { get; private set; }

    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Refresh();
        await Task.Delay(FirstCheckDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (settings.Current.Updates.AutoCheck && State is not (UpdateState.Ready or UpdateState.NotInstalled or UpdateState.NotConfigured))
            {
                await CheckAsync(stoppingToken);
            }

            await Task.Delay(CheckEvery, stoppingToken);
        }
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
        {
            return; // A check is already running.
        }

        try
        {
            var manager = Refresh();
            if (manager is null || State == UpdateState.Ready)
            {
                return;
            }

            Set(UpdateState.Checking);
            var update = await manager.CheckForUpdatesAsync();
            LastCheckedAt = DateTime.UtcNow;
            if (update is null)
            {
                Set(UpdateState.UpToDate);
                return;
            }

            Set(UpdateState.Downloading);
            await manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);
            _pending = update;
            Set(UpdateState.Ready);
            logger.LogInformation("Update {Version} downloaded", PendingVersion);
            notifier.Notify("Update ready", $"Personal Assistant {PendingVersion} is ready. Restart it from Settings when it suits you.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Update check failed");
            Error = ex.Message;
            Set(UpdateState.Failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ApplyAndRestart()
    {
        if (_manager is null || _pending is null)
        {
            throw new InvalidOperationException("No update is ready yet.");
        }

        logger.LogInformation("Restarting to install {Version}", PendingVersion);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }

    /// <summary>Builds the manager for the current settings, or sets why there isn't one.</summary>
    private UpdateManager? Refresh()
    {
        var u = settings.Current.Updates;
        if (string.IsNullOrWhiteSpace(u.GitHubRepo))
        {
            Set(UpdateState.NotConfigured);
            return null;
        }

        var token = secrets.Get(SecretNames.UpdateToken) ?? secrets.Get(SecretNames.GitHubToken);
        var key = $"{u.GitHubRepo}|{u.IncludePrerelease}|{token?.GetHashCode()}";
        if (_manager is null || _managerKey != key)
        {
            _manager = new UpdateManager(new GithubSource(u.GitHubRepo.Trim(), token, u.IncludePrerelease));
            _managerKey = key;
        }

        if (!_manager.IsInstalled)
        {
            Set(UpdateState.NotInstalled);
            return null;
        }

        CurrentVersion = _manager.CurrentVersion?.ToString() ?? CurrentVersion;
        if (State is UpdateState.NotInstalled or UpdateState.NotConfigured)
        {
            Set(UpdateState.Idle);
        }

        return _manager;
    }

    private void Set(UpdateState state)
    {
        State = state;
        if (state != UpdateState.Failed)
        {
            Error = null;
        }

        Changed?.Invoke();
    }
}
