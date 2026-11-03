using System.Net;
using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Secrets;
using Assistant.Tools.Catalog;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Mcp;

public sealed class AssistantMcpOptions
{
    public string Path { get; set; } = "/mcp";

    /// <summary>Host header values accepted. Anything else is a DNS rebinding attempt or a misconfiguration.</summary>
    public HashSet<string> AllowedHosts { get; } = new(StringComparer.OrdinalIgnoreCase) { "127.0.0.1", "localhost", "[::1]", "::1" };
}

/// <summary>The bearer token MCP clients must send. Generated on first use and kept in Credential Manager.</summary>
public sealed class McpTokenProvider(ISecretStore secrets)
{
    private readonly Lock _lock = new();
    private string? _token;

    public string GetToken()
    {
        lock (_lock)
        {
            if (_token is not null)
            {
                return _token;
            }

            _token = secrets.Get(SecretNames.McpToken);
            if (string.IsNullOrEmpty(_token))
            {
                _token = Generate();
                secrets.Set(SecretNames.McpToken, _token);
            }

            return _token;
        }
    }

    /// <summary>Issues a new token. Every connected client has to be reconfigured.</summary>
    public string Regenerate()
    {
        lock (_lock)
        {
            _token = Generate();
            secrets.Set(SecretNames.McpToken, _token);
            return _token;
        }
    }

    public bool IsValid(string? presented)
    {
        var expected = GetToken();
        return !string.IsNullOrEmpty(presented) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
    }

    private static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// Guards the MCP endpoint. Kestrel only listens on loopback, but a web page you visit can still
/// reach 127.0.0.1 from your browser, so this also checks the Host and Origin headers and requires
/// the bearer token.
/// </summary>
public sealed class McpSecurityMiddleware(
    RequestDelegate next,
    McpTokenProvider tokens,
    HandoffRegistry handoffs,
    IOptions<AssistantMcpOptions> options,
    ILogger<McpSecurityMiddleware> logger)
{
    /// <summary>Where a valid handoff key leaves its item id for the tool call handler.</summary>
    public const string HandoffItemKey = "assistant.handoffItemId";

    public async Task InvokeAsync(HttpContext context)
    {
        var settings = options.Value;
        if (!context.Request.Path.StartsWithSegments(settings.Path))
        {
            await next(context);
            return;
        }

        if (!settings.AllowedHosts.Contains(context.Request.Host.Host))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, $"Host '{context.Request.Host.Host}' is not allowed.");
            return;
        }

        var origin = context.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && !IsLoopbackOrigin(origin))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, $"Origin '{origin}' is not allowed.");
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string scheme = "Bearer ";
        var token = authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? authorization[scheme.Length..].Trim() : null;
        if (!tokens.IsValid(token))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await RejectAsync(context, StatusCodes.Status401Unauthorized, "A valid bearer token is required.");
            return;
        }

        if (handoffs.Find(context.Request.Headers[HandoffRegistry.HeaderName].ToString()) is { } itemId)
        {
            context.Items[HandoffItemKey] = itemId;
        }

        await next(context);
    }

    private async Task RejectAsync(HttpContext context, int status, string message)
    {
        logger.LogWarning("Rejected MCP request from {Remote}: {Message}", context.Connection.RemoteIpAddress, message);
        context.Response.StatusCode = status;
        await context.Response.WriteAsync(message);
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
}
