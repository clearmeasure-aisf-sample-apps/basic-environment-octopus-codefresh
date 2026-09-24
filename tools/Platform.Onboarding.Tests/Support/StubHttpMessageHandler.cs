using System.Net;
using System.Text;

namespace Platform.Onboarding.Tests.Support;

/// <summary>Answers requests from a table of path to (status, JSON body) and records the requests.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Body)> responses = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    public StubHttpMessageHandler Respond(string pathAndQuery, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        responses[pathAndQuery] = (status, json);
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        var key = request.RequestUri!.PathAndQuery;
        var (status, body) = responses.TryGetValue(key, out var found) ? found : (HttpStatusCode.NotFound, "{}");
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
