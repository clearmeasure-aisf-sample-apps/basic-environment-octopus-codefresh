using System.Net;

namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Retries read requests (GET, HEAD and OPTIONS without a body) after a transient failure: the connection dropped before
/// a response arrived (for example a proxy reset it), or the server answered 408, 429, 502, 503 or 504. It waits 1, 2 and
/// 4 seconds (or the server's <c>Retry-After</c>, at most 30 seconds) and gives up after <see cref="MaxRetries"/> retries,
/// returning the last answer or rethrowing the last failure. Other methods are sent once: a retried POST could create a
/// second release, deployment or runbook run. The caller's timeout and cancellation bound every retry.
/// </summary>
public sealed class TransientRetryHandler : DelegatingHandler
{
    /// <summary>Retries after the first attempt.</summary>
    public const int MaxRetries = 3;

    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(30);
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    /// <summary>Creates the handler without an inner handler, for pipelines that set it (KubernetesClient).</summary>
    public TransientRetryHandler()
        : this(innerHandler: null, delay: null)
    {
    }

    /// <summary>Creates the handler over <paramref name="innerHandler"/>.</summary>
    /// <param name="innerHandler">The handler that sends the requests; set later by the pipeline when <c>null</c>.</param>
    /// <param name="delay">Waits between attempts; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when omitted (unit tests pass a stub).</param>
    public TransientRetryHandler(HttpMessageHandler? innerHandler, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (innerHandler is not null)
        {
            InnerHandler = innerHandler;
        }

        this.delay = delay ?? Task.Delay;
    }

    /// <summary><c>true</c> for the methods this handler retries: GET, HEAD and OPTIONS.</summary>
    /// <param name="method">HTTP method.</param>
    public static bool IsRetriedMethod(HttpMethod method) =>
        method == HttpMethod.Get || method == HttpMethod.Head || method == HttpMethod.Options;

    /// <summary><c>true</c> for an answer worth retrying: 408, 429, 502, 503 or 504.</summary>
    /// <param name="status">HTTP status.</param>
    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// <c>true</c> for a failure worth retrying: no response arrived (connection refused or reset, the response ended early,
    /// the proxy tunnel failed). Cancellation by the caller, including the client timeout, is never retried.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception switch
        {
            HttpRequestException { StatusCode: null } failure => failure.HttpRequestError is not (HttpRequestError.UserAuthenticationError or HttpRequestError.ConfigurationLimitExceeded),
            IOException => true,
            _ => false,
        };

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsRetriedMethod(request.Method) || request.Content is not null)
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxRetries && IsTransient(ex, cancellationToken))
            {
                await delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= MaxRetries || !IsTransient(response.StatusCode))
            {
                return response;
            }

            var wait = RetryAfter(response) ?? Backoff(attempt);
            response.Dispose();
            await delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(1 << attempt);

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        var wait = header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (wait is not { } value)
        {
            return null;
        }

        return value < TimeSpan.Zero ? TimeSpan.Zero : value > MaxRetryAfter ? MaxRetryAfter : value;
    }
}
