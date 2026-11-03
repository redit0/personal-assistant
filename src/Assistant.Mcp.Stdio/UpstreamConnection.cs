using Assistant.Core;
using Assistant.Core.Secrets;
using Assistant.Core.Settings;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Assistant.Mcp.Stdio;

/// <summary>Where the tray app's MCP endpoint is and how to authenticate to it.</summary>
public sealed record UpstreamOptions(Uri Endpoint, string? Token)
{
    /// <summary>
    /// PA_MCP_URL and PA_MCP_TOKEN override the defaults. Otherwise the port comes from the app's
    /// settings file and the token from Credential Manager, both readable because the bridge runs as you.
    /// </summary>
    public static UpstreamOptions FromEnvironment()
    {
        var url = Environment.GetEnvironmentVariable("PA_MCP_URL");
        var endpoint = !string.IsNullOrWhiteSpace(url)
            ? new Uri(url)
            : new Uri($"http://127.0.0.1:{new UserSettingsStore(AppPaths.SettingsFile).Current.Mcp.Port}/mcp");

        var token = Environment.GetEnvironmentVariable("PA_MCP_TOKEN");
        return new UpstreamOptions(endpoint, string.IsNullOrWhiteSpace(token) ? null : token);
    }
}

/// <summary>
/// The bridge's connection to the tray app. Connects on first use, names itself after the client that
/// launched the bridge (so the audit log says "claude-ai", not "bridge"), and reconnects once if the
/// tray app was restarted.
/// </summary>
public sealed class UpstreamConnection(
    UpstreamOptions options,
    Func<HttpClient> httpClientFactory,
    ISecretStore secrets,
    ILoggerFactory loggerFactory) : IAsyncDisposable
{
    /// <summary>Long enough to cover an approval prompt, which waits up to two minutes.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger _logger = loggerFactory.CreateLogger<UpstreamConnection>();
    private McpClient? _client;

    public Task<ListToolsResult> ListToolsAsync(Implementation? downstream, CancellationToken cancellationToken) =>
        WithClientAsync(downstream, (c, ct) => c.ListToolsAsync(new ListToolsRequestParams(), ct).AsTask(), safeToRepeat: true, cancellationToken);

    public Task<CallToolResult> CallToolAsync(CallToolRequestParams request, Implementation? downstream, CancellationToken cancellationToken) =>
        WithClientAsync(downstream, (c, ct) => c.CallToolAsync(request, ct).AsTask(), safeToRepeat: false, cancellationToken);

    public Task<ListPromptsResult> ListPromptsAsync(Implementation? downstream, CancellationToken cancellationToken) =>
        WithClientAsync(downstream, (c, ct) => c.ListPromptsAsync(new ListPromptsRequestParams(), ct).AsTask(), safeToRepeat: true, cancellationToken);

    public Task<GetPromptResult> GetPromptAsync(GetPromptRequestParams request, Implementation? downstream, CancellationToken cancellationToken) =>
        WithClientAsync(downstream, (c, ct) => c.GetPromptAsync(request, ct).AsTask(), safeToRepeat: true, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    private async Task<T> WithClientAsync<T>(
        Implementation? downstream,
        Func<McpClient, CancellationToken, Task<T>> call,
        bool safeToRepeat,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var client = await GetClientAsync(downstream, cancellationToken);
                return await call(client, cancellationToken);
            }
            catch (Exception ex) when (IsConnectionFailure(ex) && !cancellationToken.IsCancellationRequested)
            {
                await ResetAsync();

                // A call that may have reached the app (a timeout, a dropped response) is not repeated,
                // because repeating create_item would create two items.
                if (attempt >= 2 || (!safeToRepeat && !NeverReachedServer(ex)))
                {
                    _logger.LogWarning(ex, "Upstream call failed");
                    throw new McpException(
                        $"Can't reach Personal Assistant at {options.Endpoint}. Make sure the tray app is running " +
                        $"and its MCP server is enabled in Settings. ({ex.Message})", ex);
                }
            }
        }
    }

    private async Task<McpClient> GetClientAsync(Implementation? downstream, CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var token = options.Token ?? secrets.Get(SecretNames.McpToken)
                ?? throw new McpException("No MCP token found. Start the Personal Assistant tray app once to create it.");

            var http = httpClientFactory();
            http.Timeout = RequestTimeout;

            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = options.Endpoint,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
                },
                http,
                loggerFactory,
                ownsHttpClient: true);

            _client = await McpClient.CreateAsync(
                transport,
                new McpClientOptions
                {
                    ClientInfo = new Implementation
                    {
                        Name = string.IsNullOrWhiteSpace(downstream?.Name) ? "assistant-mcp-bridge" : downstream.Name,
                        Title = downstream?.Title,
                        Version = string.IsNullOrWhiteSpace(downstream?.Version) ? "0" : downstream.Version,
                    },
                },
                loggerFactory,
                cancellationToken);

            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ResetAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is not null)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring error while disposing a dead upstream connection");
            }
        }
    }

    // Tool errors come back as results, not exceptions, so anything thrown here means the transport broke.
    private static bool IsConnectionFailure(Exception ex) =>
        ex is HttpRequestException or IOException or TimeoutException or ObjectDisposedException ||
        (ex is McpException && ex.InnerException is HttpRequestException or IOException) ||
        ex is TaskCanceledException;

    private static bool NeverReachedServer(Exception ex) =>
        ex is ObjectDisposedException ||
        (ex as HttpRequestException ?? ex.InnerException as HttpRequestException)?.HttpRequestError == HttpRequestError.ConnectionError;
}
