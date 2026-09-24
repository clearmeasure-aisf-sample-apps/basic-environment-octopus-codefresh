using System.Net;
using System.Text;

namespace Platform.Conformance.Offline.Support;

/// <summary>Answers HTTP requests from a routing function and records each request with its body.</summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> respond;

    public StubHttpMessageHandler(Func<RecordedRequest, HttpResponseMessage> respond)
    {
        this.respond = respond;
    }

    public List<RecordedRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers
            .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
        var recorded = new RecordedRequest(request.Method.Method, request.RequestUri!, headers, body);
        Requests.Add(recorded);
        return respond(recorded);
    }
}

/// <summary>A request seen by <see cref="StubHttpMessageHandler"/>.</summary>
internal sealed record RecordedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string PathAndQuery => Uri.PathAndQuery;

    public string? Header(string name) => Headers.GetValueOrDefault(name);
}
