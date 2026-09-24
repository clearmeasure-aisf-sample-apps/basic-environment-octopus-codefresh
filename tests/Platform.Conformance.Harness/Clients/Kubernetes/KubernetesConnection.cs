using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Azure.Core;
using Azure.Identity;
using k8s;
using k8s.Authentication;
using YamlDotNet.RepresentationModel;

namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Builds the Kubernetes client configuration of an AKS cluster: the server and CA come from the ARM user kubeconfig,
/// authentication is an Entra bearer token for the AKS server application, and TLS is always verified.
/// </summary>
public static class KubernetesConnection
{
    /// <summary>Token scope of the AKS Entra server application (the same for every AKS cluster).</summary>
    public const string AksServerScope = "6dae42f8-4368-4678-94ff-3960e28e3630/.default";

    /// <summary>
    /// Creates the configuration. With <paramref name="systemTrust"/> the server certificate is validated against the system
    /// trust store (for a TLS-re-terminating proxy whose CA is trusted there); otherwise the cluster CA is pinned.
    /// TLS verification is never skipped, and no client certificate or kubeconfig user is used.
    /// </summary>
    /// <param name="endpoint">Server and CA from the kubeconfig.</param>
    /// <param name="tokenProvider">Supplies the bearer token for each request.</param>
    /// <param name="systemTrust"><c>true</c> when <c>PLATFORM_TLS_SYSTEM_TRUST=true</c>.</param>
    /// <exception cref="InvalidOperationException">The CA is needed (no system trust) but the kubeconfig has none.</exception>
    public static KubernetesClientConfiguration CreateConfiguration(KubernetesEndpoint endpoint, ITokenProvider tokenProvider, bool systemTrust)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        var configuration = new KubernetesClientConfiguration
        {
            Host = endpoint.Server,
            TokenProvider = tokenProvider,
            SkipTlsVerify = false,
        };
        if (!systemTrust)
        {
            if (string.IsNullOrWhiteSpace(endpoint.CertificateAuthorityPem))
            {
                throw new InvalidOperationException(
                    $"The kubeconfig of {endpoint.ClusterName} has no certificate-authority-data to pin. Behind a TLS-re-terminating proxy whose CA the system trusts, set PLATFORM_TLS_SYSTEM_TRUST=true.");
            }

            var authorities = new X509Certificate2Collection();
            authorities.ImportFromPem(endpoint.CertificateAuthorityPem);
            configuration.SslCaCerts = authorities;
        }

        return configuration;
    }

    /// <summary>
    /// Makes a stopped cluster an Inconclusive result with guidance instead of a connection error: env-sleep stops the app
    /// clusters outside the working window, and platform-env/conformance-arm stops both before a nightly run, so a test that
    /// reads a cluster must run after something woke it.
    /// </summary>
    /// <param name="azure">Azure client that reads the power state.</param>
    /// <param name="resourceGroup">Resource group of the cluster.</param>
    /// <param name="clusterName">Cluster name.</param>
    /// <param name="cancellationToken">Cancels the ARM call.</param>
    /// <exception cref="PlatformPrerequisiteException">The cluster is stopped or stopping (the test becomes Inconclusive).</exception>
    public static async Task RequireRunningAsync(IAzureApi azure, string resourceGroup, string clusterName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(azure);
        var state = await azure.GetClusterStateAsync(resourceGroup, clusterName, cancellationToken).ConfigureAwait(false);
        if (string.Equals(state.PowerState, "Stopped", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state.ProvisioningState, "Stopping", StringComparison.OrdinalIgnoreCase))
        {
            throw new PlatformPrerequisiteException(
                $"Cluster {clusterName} is not running ({state}), so its Kubernetes API cannot answer. env-sleep stops the app clusters outside the working "
                + "window and platform-env/conformance-arm stops both before a run: wake it with runbook env-wake of platform-infrastructure in the tier's "
                + "infra environment, or run this test after one that wakes the tier.");
        }
    }

    /// <summary>Reads the first cluster entry (server and CA) of a kubeconfig; the users section is ignored.</summary>
    /// <param name="kubeconfig">Kubeconfig YAML as returned by ARM.</param>
    /// <exception cref="InvalidOperationException">The kubeconfig has no cluster with a server.</exception>
    public static KubernetesEndpoint ReadEndpoint(byte[] kubeconfig)
    {
        ArgumentNullException.ThrowIfNull(kubeconfig);
        var stream = new YamlStream();
        stream.Load(new StringReader(Encoding.UTF8.GetString(kubeconfig)));
        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("clusters"), out var clusters)
            || clusters is not YamlSequenceNode { Children.Count: > 0 } list
            || list.Children[0] is not YamlMappingNode entry)
        {
            throw new InvalidOperationException("The kubeconfig has no clusters entry.");
        }

        var name = Scalar(entry, "name") ?? "cluster";
        if (!entry.Children.TryGetValue(new YamlScalarNode("cluster"), out var node) || node is not YamlMappingNode cluster || Scalar(cluster, "server") is not { } server)
        {
            throw new InvalidOperationException($"The kubeconfig cluster {name} has no server.");
        }

        var authorityData = Scalar(cluster, "certificate-authority-data");
        var pem = string.IsNullOrWhiteSpace(authorityData) ? null : Encoding.UTF8.GetString(Convert.FromBase64String(authorityData));
        return new KubernetesEndpoint(name, server, pem);
    }

    private static string? Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode { Value: { Length: > 0 } text } ? text : null;
}

/// <summary>Supplies Entra bearer tokens for the AKS server application to the Kubernetes client.</summary>
public sealed class EntraTokenProvider : ITokenProvider
{
    private readonly TokenCredential credential;

    /// <summary>Creates the provider.</summary>
    /// <param name="credential">Azure credential with access to the cluster (Azure RBAC).</param>
    public EntraTokenProvider(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        this.credential = credential;
    }

    /// <inheritdoc />
    public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([KubernetesConnection.AksServerScope]), cancellationToken).ConfigureAwait(false);
            return new AuthenticationHeaderValue("Bearer", token.Token);
        }
        catch (CredentialUnavailableException ex)
        {
            throw new PlatformPrerequisiteException(AzureCredentialFactory.MissingCredentialMessage(ex.Message), ex);
        }
    }
}
