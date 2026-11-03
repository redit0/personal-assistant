using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Assistant.Core.Connectors;
using Microsoft.Identity.Client;

namespace Assistant.Connectors.Microsoft;

public sealed record WorkIqSource(string? Title, string? Url, string? Provider);

public sealed record WorkIqAnswer(string ConversationId, string Text, IReadOnlyList<WorkIqSource> Sources);

/// <summary>
/// Microsoft Work IQ, which answers questions grounded in your mail, meetings, chats, and files. It needs a one-time
/// admin setup (the Work IQ service principal and consent to WorkIQAgent.Ask) and a Copilot Credits billing policy
/// that covers you. A Microsoft 365 Copilot license isn't required.
/// </summary>
public sealed class WorkIqClient(IHttpClientFactory httpClients, IMicrosoftTokenProvider auth)
{
    public const string HttpClientName = "workiq";

    public static readonly string[] Scopes = ["api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask"];

    private static readonly Uri Base = new("https://workiq.svc.cloud.microsoft/rest/");

    private const string AdminHelp =
        "Work IQ isn't set up for your account yet. Your Microsoft 365 admin needs to add the Work IQ service principal, " +
        "grant admin consent for WorkIQAgent.Ask on your Entra app, and add you to a Copilot Credits spending policy that includes the Work IQ API. " +
        "See the Work IQ section on the Connectors page.";

    /// <summary>Asks a question, continuing <paramref name="conversationId"/> when given so follow-ups keep their context.</summary>
    public async Task<WorkIqAnswer> AskAsync(string question, string? conversationId, bool webGrounding, CancellationToken cancellationToken)
    {
        var token = await TokenAsync(cancellationToken);
        using var http = httpClients.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromMinutes(2);

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            using var created = await SendAsync(http, token, "conversations", new { }, cancellationToken);
            conversationId = created.RootElement.GetProperty("id").GetString()!;
        }

        var body = new
        {
            message = new { text = question },
            locationHint = new { timeZone = IanaTimeZone() },
            contextualResources = new { webContext = new { isWebEnabled = webGrounding } },
        };

        using var reply = await SendAsync(http, token, $"conversations/{Uri.EscapeDataString(conversationId)}/chat", body, cancellationToken);
        return Parse(conversationId, reply.RootElement);
    }

    /// <summary>The answer is the last message in the conversation. Public for tests.</summary>
    public static WorkIqAnswer Parse(string conversationId, JsonElement root)
    {
        var messages = root.TryGetProperty("messages", out var m) && m.ValueKind == JsonValueKind.Array ? m.EnumerateArray().ToList() : [];
        var last = messages.Count > 0 ? messages[^1] : default;
        var text = last.ValueKind == JsonValueKind.Object && last.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var sources = last.ValueKind == JsonValueKind.Object && last.TryGetProperty("attributions", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
                .Select(x => new WorkIqSource(Str(x, "providerDisplayName"), Str(x, "seeMoreWebUrl"), Str(x, "attributionSource")))
                .Where(x => x.Url is not null)
                .DistinctBy(x => x.Url)
                .ToList()
            : [];
        return new WorkIqAnswer(conversationId, text, sources);
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await auth.GetAccessTokenAsync(Scopes, cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            throw new ConnectorException("Work IQ needs your consent. Click Connect Work IQ on the Connectors page.");
        }
        catch (MsalServiceException ex) when (ex.Message.Contains("AADSTS65001") || ex.Message.Contains("AADSTS650057")
            || ex.Message.Contains("AADSTS500011") || ex.Message.Contains("AADSTS90094"))
        {
            throw new ConnectorException(AdminHelp);
        }
    }

    private static async Task<JsonDocument> SendAsync(HttpClient http, string token, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, path)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ConnectorException("Work IQ didn't accept your sign-in. Click Connect Work IQ on the Connectors page."),
            HttpStatusCode.Forbidden => new ConnectorException(AdminHelp + $" ({Short(text)})"),
            HttpStatusCode.TooManyRequests => new ConnectorException("Work IQ is throttling requests. Try again in a minute."),
            HttpStatusCode.GatewayTimeout or HttpStatusCode.RequestTimeout => new ConnectorException("Work IQ took too long to answer. Try a narrower question."),
            _ => new ConnectorException($"Work IQ returned {(int)response.StatusCode}: {Short(text)}"),
        };
    }

    private static string IanaTimeZone() =>
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : TimeZoneInfo.Local.Id;

    private static string Short(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
