using System.Net;
using System.Text;

namespace Assistant.Mcp.Tests.Connectors;

/// <summary>Answers HTTP requests from canned responses and records what was asked.</summary>
public sealed class FakeHttp : HttpMessageHandler, IHttpClientFactory
{
    private readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public FakeHttp On(Func<HttpRequestMessage, bool> match, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((match, _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));
        return this;
    }

    /// <summary>For an answer that changes between requests.</summary>
    public FakeHttp On(Func<HttpRequestMessage, bool> match, Func<string> json)
    {
        _routes.Add((match, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json(), Encoding.UTF8, "application/json") }));
        return this;
    }

    public FakeHttp OnUrl(string contains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(r => Uri.UnescapeDataString(r.RequestUri!.ToString()).Contains(contains, StringComparison.Ordinal), json, status);

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));

        // Last registered route wins, so tests can override a default.
        for (var i = _routes.Count - 1; i >= 0; i--)
        {
            if (_routes[i].Match(request))
            {
                return _routes[i].Respond(request);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"No fake route for {request.RequestUri}") };
    }
}
