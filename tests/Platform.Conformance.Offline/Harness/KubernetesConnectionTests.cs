using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NUnit.Framework.Interfaces;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Offline.Support;

namespace Platform.Conformance.Offline.Harness;

/// <summary>Proves cluster connections: TLS always verified (pinned CA or system trust), Entra bearer tokens, no client certificates.</summary>
[TestFixture]
[Category(Categories.Offline)]
public class KubernetesConnectionTests
{
    private ECDsa authorityKey = null!;
    private X509Certificate2 authority = null!;
    private byte[] kubeconfig = null!;

    [OneTimeSetUp]
    public void CreateClusterAuthorityAndKubeconfig()
    {
        authorityKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        authority = CreateAuthority("CN=cluster-ca", authorityKey);
        var authorityData = Convert.ToBase64String(Encoding.UTF8.GetBytes(authority.ExportCertificatePem()));
        kubeconfig = Encoding.UTF8.GetBytes($"""
            apiVersion: v1
            kind: Config
            clusters:
              - name: aks-platform-nonprod
                cluster:
                  server: https://aks-platform-nonprod-dns.hcp.example.test:443
                  certificate-authority-data: {authorityData}
            contexts:
              - name: aks-platform-nonprod
                context:
                  cluster: aks-platform-nonprod
                  user: clusterUser_rg-platform-nonprod-aks_aks-platform-nonprod
            current-context: aks-platform-nonprod
            users:
              - name: clusterUser_rg-platform-nonprod-aks_aks-platform-nonprod
                user:
                  exec:
                    apiVersion: client.authentication.k8s.io/v1beta1
                    command: kubelogin
                    args: [get-token, --server-id, 6dae42f8-4368-4678-94ff-3960e28e3630]
            """);
    }

    [OneTimeTearDown]
    public void DisposeAuthority()
    {
        authority.Dispose();
        authorityKey.Dispose();
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public void WhenReadEndpoint_AksUserKubeconfig_ReadsServerAndAuthorityOnly()
    {
        var endpoint = KubernetesConnection.ReadEndpoint(kubeconfig);

        endpoint.ClusterName.ShouldBe("aks-platform-nonprod");
        endpoint.Server.ShouldBe("https://aks-platform-nonprod-dns.hcp.example.test:443");
        endpoint.CertificateAuthorityPem.ShouldBe(authority.ExportCertificatePem());
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public void WhenCreateConfiguration_DefaultMode_PinsTheClusterAuthorityAndUsesTheTokenProvider()
    {
        var tokenProvider = new StubTokenProvider();

        var configuration = KubernetesConnection.CreateConfiguration(KubernetesConnection.ReadEndpoint(kubeconfig), tokenProvider, systemTrust: false);

        configuration.SkipTlsVerify.ShouldBeFalse();
        configuration.SslCaCerts.ShouldNotBeNull().ShouldHaveSingleItem().Thumbprint.ShouldBe(authority.Thumbprint);
        configuration.TokenProvider.ShouldBeSameAs(tokenProvider);
        configuration.ClientCertificateData.ShouldBeNull();
        configuration.ClientCertificateKeyData.ShouldBeNull();
        configuration.AccessToken.ShouldBeNull();
        configuration.Host.ShouldBe("https://aks-platform-nonprod-dns.hcp.example.test:443");
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public void WhenCreateConfiguration_SystemTrustMode_ValidatesAgainstTheSystemStoreWithoutPinning()
    {
        var configuration = KubernetesConnection.CreateConfiguration(KubernetesConnection.ReadEndpoint(kubeconfig), new StubTokenProvider(), systemTrust: true);

        configuration.SslCaCerts.ShouldBeNull();
        configuration.SkipTlsVerify.ShouldBeFalse();
        configuration.ClientCertificateData.ShouldBeNull();
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public void WhenCreateConfiguration_NoAuthorityAndNoSystemTrust_ThrowsInsteadOfConnectingUnverified()
    {
        var endpoint = new KubernetesEndpoint("aks-platform-prod", "https://aks-platform-prod.example.test:443", null);

        var exception = Should.Throw<InvalidOperationException>(() => KubernetesConnection.CreateConfiguration(endpoint, new StubTokenProvider(), systemTrust: false));

        exception.Message.ShouldContain("PLATFORM_TLS_SYSTEM_TRUST=true");
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public void WhenKubernetesClientIsBuilt_EitherMode_VerifiesTlsAndSendsNoClientCertificate()
    {
        var endpoint = KubernetesConnection.ReadEndpoint(kubeconfig);
        var pinnedConfiguration = KubernetesConnection.CreateConfiguration(endpoint, new StubTokenProvider(), systemTrust: false);
        var systemConfiguration = KubernetesConnection.CreateConfiguration(endpoint, new StubTokenProvider(), systemTrust: true);
        SocketsHttpHandler? pinnedHandler = null;
        SocketsHttpHandler? systemHandler = null;
        pinnedConfiguration.FirstMessageHandlerSetup = handler => pinnedHandler = (SocketsHttpHandler)handler;
        systemConfiguration.FirstMessageHandlerSetup = handler => systemHandler = (SocketsHttpHandler)handler;

        using var pinned = new k8s.Kubernetes(pinnedConfiguration);
        using var systemTrust = new k8s.Kubernetes(systemConfiguration);

        pinned.Credentials.ShouldBeOfType<k8s.Authentication.TokenCredentials>();
        pinnedHandler.ShouldNotBeNull().SslOptions.RemoteCertificateValidationCallback.ShouldNotBeNull();
        pinnedHandler.SslOptions.ClientCertificates.ShouldNotBeNull().Count.ShouldBe(0);
        systemHandler.ShouldNotBeNull().SslOptions.RemoteCertificateValidationCallback.ShouldBeNull();
        systemHandler.SslOptions.ClientCertificates.ShouldNotBeNull().Count.ShouldBe(0);
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public void WhenPinnedValidationRuns_CertificateOfAnotherAuthority_IsRejectedAndClusterCertificateAccepted()
    {
        var configuration = KubernetesConnection.CreateConfiguration(KubernetesConnection.ReadEndpoint(kubeconfig), new StubTokenProvider(), systemTrust: false);
        SocketsHttpHandler? handler = null;
        configuration.FirstMessageHandlerSetup = setup => handler = (SocketsHttpHandler)setup;
        using var client = new k8s.Kubernetes(configuration);
        var validate = handler.ShouldNotBeNull().SslOptions.RemoteCertificateValidationCallback.ShouldNotBeNull();
        using var strangerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var stranger = CreateAuthority("CN=some-other-ca", strangerKey);
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var server = new CertificateRequest("CN=aks-platform-nonprod-dns.hcp.example.test", serverKey, HashAlgorithmName.SHA256)
            .Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1), [1, 2, 3, 4]);
        using var strangerChain = new X509Chain();
        using var serverChain = new X509Chain();

        var strangerAccepted = validate(this, stranger, strangerChain, SslPolicyErrors.RemoteCertificateChainErrors);
        var serverAccepted = validate(this, server, serverChain, SslPolicyErrors.RemoteCertificateChainErrors);

        strangerAccepted.ShouldBeFalse();
        serverAccepted.ShouldBeTrue();
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public async Task WhenGetAuthenticationHeaderAsync_Credential_RequestsAnEntraTokenForTheAksServerApplication()
    {
        var credential = StubTokenCredential.Returning("<stub-entra-token>");
        var provider = new EntraTokenProvider(credential);

        var header = await provider.GetAuthenticationHeaderAsync(CancellationToken.None);

        header.Scheme.ShouldBe("Bearer");
        header.Parameter.ShouldBe("<stub-entra-token>");
        credential.RequestedScopes.ShouldBe(["6dae42f8-4368-4678-94ff-3960e28e3630/.default"]);
    }

    [Test]
    [Capability("CAP-HARNESS-010")]
    public async Task WhenGetAuthenticationHeaderAsync_NoCredentialAvailable_ThrowsInconclusive()
    {
        var provider = new EntraTokenProvider(StubTokenCredential.Unavailable());

        var exception = await Should.ThrowAsync<PlatformPrerequisiteException>(() => provider.GetAuthenticationHeaderAsync(CancellationToken.None));

        exception.ResultState.ShouldBe(ResultState.Inconclusive);
    }

    private static X509Certificate2 CreateAuthority(string subject, ECDsa key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddDays(2));
    }
}
