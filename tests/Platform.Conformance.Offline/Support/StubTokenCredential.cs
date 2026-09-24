using Azure.Core;
using Azure.Identity;

namespace Platform.Conformance.Offline.Support;

/// <summary>An Azure credential that returns a fixed token (or reports that no credential is available) and records the scopes asked for.</summary>
internal sealed class StubTokenCredential : TokenCredential
{
    private readonly string? token;

    private StubTokenCredential(string? token)
    {
        this.token = token;
    }

    public List<string> RequestedScopes { get; } = [];

    public static StubTokenCredential Returning(string token) => new(token);

    public static StubTokenCredential Unavailable() => new(null);

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        RequestedScopes.AddRange(requestContext.Scopes);
        return token is null
            ? throw new CredentialUnavailableException("StubTokenCredential has no credential.")
            : new AccessToken(token, DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}
