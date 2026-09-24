using System.Net.Http.Headers;
using k8s.Authentication;

namespace Platform.Conformance.Offline.Support;

/// <summary>A Kubernetes token provider that returns a fixed bearer token.</summary>
internal sealed class StubTokenProvider : ITokenProvider
{
    public Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AuthenticationHeaderValue("Bearer", "<stub-entra-token>"));
}
