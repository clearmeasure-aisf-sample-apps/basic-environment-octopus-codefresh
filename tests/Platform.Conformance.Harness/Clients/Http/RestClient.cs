using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Platform.Conformance.Harness.Clients;

/// <summary>JSON over HTTP for one platform system; turns every failure into a <see cref="PlatformApiException"/>.</summary>
internal sealed class RestClient : IDisposable
{
    private const int MaxDetailLength = 2000;
    private readonly HttpClient http;
    private readonly JsonSerializerOptions json;

    public RestClient(HttpClient http, string system, JsonSerializerOptions json)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(system);
        ArgumentNullException.ThrowIfNull(json);
        if (http.BaseAddress is null || !http.BaseAddress.AbsoluteUri.EndsWith('/'))
        {
            throw new ArgumentException("The HttpClient needs a BaseAddress that ends with '/'.", nameof(http));
        }

        this.http = http;
        this.json = json;
        System = system;
    }

    public string System { get; }

    public Uri BaseAddress => http.BaseAddress!;

    public Task<T> GetAsync<T>(string path, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null) =>
        SendAsync<T>(HttpMethod.Get, path, body: null, cancellationToken, configure);

    public async Task<string> GetStringAsync(string path, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Relative(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        configure?.Invoke(request);
        using var response = await SendCoreAsync(request, path, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        using var request = CreateRequest(method, path, body, configure);
        using var response = await SendCoreAsync(request, path, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, method, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken, Action<HttpRequestMessage>? configure = null)
    {
        using var request = CreateRequest(method, path, body, configure);
        using var response = await SendCoreAsync(request, path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> PostFormAsync<T>(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative(path)) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await SendCoreAsync(request, path, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync<T>(response, HttpMethod.Post, path, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => http.Dispose();

    private static Uri Relative(string path) => new(path.TrimStart('/'), UriKind.Relative);

    private static string Truncate(string text) =>
        text.Length <= MaxDetailLength ? text : string.Concat(text.AsSpan(0, MaxDetailLength), "…");

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body, Action<HttpRequestMessage>? configure)
    {
        var request = new HttpRequestMessage(method, Relative(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: json);
        }

        configure?.Invoke(request);
        return request;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, string path, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PlatformApiException(System, request.Method.Method, path, ex.StatusCode, ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PlatformApiException(System, request.Method.Method, path, null, $"timed out after {http.Timeout}", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new PlatformApiException(System, request.Method.Method, path, response.StatusCode, Truncate(string.IsNullOrWhiteSpace(detail) ? response.ReasonPhrase ?? "(empty body)" : detail));
        }
    }

    private async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(text, json)
                ?? throw new JsonException("the body was empty or null");
        }
        catch (JsonException ex)
        {
            throw new PlatformApiException(System, method.Method, path, response.StatusCode, $"the response could not be read as {typeof(T).Name} ({ex.Message}): {Truncate(text)}", ex);
        }
    }
}

/// <summary>Builds <see cref="HttpClient"/> instances for the platform clients.</summary>
public static class PlatformHttp
{
    /// <summary>User agent sent with every request.</summary>
    public const string UserAgent = "platform-conformance-harness/1.0";

    /// <summary>
    /// Creates an <see cref="HttpClient"/> with the given base address (a trailing <c>/</c> is added), timeout and user agent.
    /// Certificate validation is the platform default (system trust store) and the proxy comes from <c>HTTPS_PROXY</c>;
    /// neither is ever relaxed. Without <paramref name="handler"/>, reads that fail transiently (a proxy that resets the
    /// connection, a 502, 503 or 504) are retried by <see cref="TransientRetryHandler"/>.
    /// </summary>
    /// <param name="baseAddress">API base, for example <c>https://example.octopus.app/api/</c>.</param>
    /// <param name="timeout">Request timeout, retries included.</param>
    /// <param name="handler">Message handler; a <see cref="SocketsHttpHandler"/> behind a <see cref="TransientRetryHandler"/> when omitted. Unit tests pass a stub.</param>
    /// <param name="bearerToken">Returns the bearer token to send with each request (read per request, so a rewritten token is picked up); the caller sets the header itself when omitted.</param>
    public static HttpClient Create(Uri baseAddress, TimeSpan timeout, HttpMessageHandler? handler = null, Func<string?>? bearerToken = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        var normalized = baseAddress.AbsoluteUri.EndsWith('/') ? baseAddress : new Uri(baseAddress.AbsoluteUri + "/");
        HttpMessageHandler pipeline = handler ?? new TransientRetryHandler(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
        if (bearerToken is not null)
        {
            pipeline = new BearerTokenHandler(bearerToken, pipeline);
        }

        var client = new HttpClient(pipeline, disposeHandler: true)
        {
            BaseAddress = normalized,
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }
}
