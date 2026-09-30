namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Sends the bearer token that <c>tokenSource</c> returns at the moment of each request, so a token that is rewritten
/// while a long suite runs (a GitHub App installation token lives one hour) is picked up without a restart. No token
/// leaves the request headers; nothing is logged.
/// </summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly Func<string?> tokenSource;

    /// <summary>Creates the handler over <paramref name="innerHandler"/>.</summary>
    /// <param name="tokenSource">Returns the current token, or <c>null</c> to leave the request as it is.</param>
    /// <param name="innerHandler">The handler that sends the requests.</param>
    public BearerTokenHandler(Func<string?> tokenSource, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(tokenSource);
        this.tokenSource = tokenSource;
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (tokenSource() is { Length: > 0 } token)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
