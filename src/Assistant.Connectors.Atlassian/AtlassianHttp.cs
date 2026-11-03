using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assistant.Core.Connectors;

namespace Assistant.Connectors.Atlassian;

/// <summary>Shared request and error handling for Jira and Bitbucket Cloud.</summary>
internal static class AtlassianHttp
{
    public static AuthenticationHeaderValue Basic(string email, string token) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));

    public static async Task<JsonDocument> SendAsync(HttpClient http, HttpRequestMessage request, AuthenticationHeaderValue auth, string product, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = auth;
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new ConnectorException($"{product} rejected the email and API token. Check both, or create a new token."),
                HttpStatusCode.Forbidden => new ConnectorException($"{product} refused the request. The token may be missing a scope. ({Short(body)})"),
                HttpStatusCode.TooManyRequests => new ConnectorException($"{product} rate limit reached. It will retry on the next sync."),
                _ => new ConnectorException($"{product} returned {(int)response.StatusCode} for {request.RequestUri?.AbsolutePath}: {Short(body)}"),
            };
        }

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    public static string? Truncate(string? text, int max = 8000) =>
        text is null || text.Length <= max ? text : text[..max] + "\n…";

    public static string Short(string text) => text.Length <= 200 ? text : text[..200] + "…";

    public static string? GetString(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement? Get(this JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind != JsonValueKind.Null ? v : null;
}
