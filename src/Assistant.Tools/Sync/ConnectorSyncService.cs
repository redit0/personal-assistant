using System.Collections.Concurrent;
using Assistant.Core.Connectors;
using Assistant.Core.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Tools.Sync;

/// <summary>What the Settings page shows for each connector.</summary>
public sealed record ConnectorStatus(
    string Id,
    string DisplayName,
    string? NotReadyReason,
    bool Running,
    DateTime? LastSuccess,
    DateTime? LastAttempt,
    string? LastError,
    int? OpenItems,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Runs each ready connector on its interval, one at a time, and applies the results with
/// <see cref="SyncReconciler"/>. Sync now requests from Settings jump the queue.
/// </summary>
public sealed class ConnectorSyncService(
    IEnumerable<IConnector> connectors,
    IServiceScopeFactory scopes,
    INotifier notifier,
    TimeProvider time,
    ILogger<ConnectorSyncService> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    private readonly IReadOnlyList<IConnector> _connectors = [.. connectors];
    private readonly ConcurrentDictionary<string, ConnectorStatus> _status = new();
    private readonly ConcurrentQueue<string> _requested = new();
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Raised when any connector's status changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised after a sync changed items, so open views can refresh.</summary>
    public event Action<string>? ItemsChanged;

    public IReadOnlyList<IConnector> Connectors => _connectors;

    public IReadOnlyList<ConnectorStatus> GetStatuses() =>
        [.. _connectors.Select(c => _status.GetValueOrDefault(c.Id) is { } s
            ? s with { NotReadyReason = c.NotReadyReason() }
            : new ConnectorStatus(c.Id, c.DisplayName, c.NotReadyReason(), false, null, null, null, null, []))];

    /// <summary>Runs a connector as soon as possible, or all ready connectors when id is null.</summary>
    public void RequestSync(string? id = null)
    {
        foreach (var connector in _connectors.Where(c => id is null || c.Id == id))
        {
            _requested.Enqueue(connector.Id);
        }

        _wake.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment to finish starting before the first round of network calls.
        await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var due = new List<IConnector>();
            while (_requested.TryDequeue(out var id))
            {
                if (_connectors.FirstOrDefault(c => c.Id == id) is { } requested && !due.Contains(requested))
                {
                    due.Add(requested);
                }
            }

            var now = time.GetUtcNow().UtcDateTime;
            due.AddRange(_connectors.Where(c => !due.Contains(c) && IsDue(c, now)));

            foreach (var connector in due.Where(c => c.NotReadyReason() is null))
            {
                await SyncAsync(connector, stoppingToken);
            }

            await _wake.WaitAsync(Tick, stoppingToken);
        }
    }

    private bool IsDue(IConnector connector, DateTime now) =>
        _status.GetValueOrDefault(connector.Id)?.LastAttempt is not { } last || now - last >= connector.Interval;

    public async Task SyncAsync(IConnector connector, CancellationToken cancellationToken)
    {
        var previous = _status.GetValueOrDefault(connector.Id);
        var firstSync = previous?.LastSuccess is null;
        var started = time.GetUtcNow().UtcDateTime;
        Update(connector, s => s with { Running = true, LastAttempt = started });

        try
        {
            var snapshot = await connector.FetchAsync(cancellationToken);

            await using var scope = scopes.CreateAsyncScope();
            var reconciler = ActivatorUtilities.CreateInstance<SyncReconciler>(scope.ServiceProvider);
            var result = await reconciler.ApplyAsync(connector.Id, snapshot, connector.DisplayName, cancellationToken);

            var warnings = snapshot.Warnings.Concat(snapshot.FailedScopes.Select(s => $"Couldn't load {s}; its items were left as they were.")).ToList();
            Update(connector, s => s with
            {
                Running = false,
                LastSuccess = started,
                LastError = null,
                OpenItems = snapshot.Items.Count,
                Warnings = warnings,
            });

            logger.LogInformation("{Connector} sync: {Count} open at source, +{Added} ~{Updated} -{Closed} reopened {Reopened} merged {Merged}",
                connector.Id, snapshot.Items.Count, result.Added, result.Updated, result.Closed, result.Reopened, result.Merged);

            if (result.Added > 0)
            {
                notifier.Notify(
                    firstSync ? $"Imported {result.Added} from {connector.DisplayName}" : $"{result.Added} new from {connector.DisplayName}",
                    string.Join("\n", result.AddedTitles.Take(3)) + (result.Added > 3 ? $"\nand {result.Added - 3} more" : ""));
            }

            if (result.Changed)
            {
                ItemsChanged?.Invoke(connector.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var message = ex is ConnectorException or HttpRequestException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            logger.LogWarning(ex, "{Connector} sync failed", connector.Id);
            Update(connector, s => s with { Running = false, LastError = message });

            // Tell you once when a working connector starts failing, not every few minutes.
            if (previous?.LastError is null)
            {
                notifier.Notify($"{connector.DisplayName} sync failed", message, NotificationKind.Warning);
            }
        }
    }

    private void Update(IConnector connector, Func<ConnectorStatus, ConnectorStatus> change)
    {
        _status.AddOrUpdate(
            connector.Id,
            _ => change(new ConnectorStatus(connector.Id, connector.DisplayName, null, false, null, null, null, null, [])),
            (_, current) => change(current));
        StatusChanged?.Invoke();
    }
}
