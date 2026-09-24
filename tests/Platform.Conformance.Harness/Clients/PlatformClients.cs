using Azure.Core;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Creates the platform clients on first use and checks their prerequisites. A client whose secret or setting is
/// missing is never created: the accessor throws <see cref="PlatformPrerequisiteException"/>, so the test is Inconclusive.
/// </summary>
public sealed class PlatformClients : IDisposable
{
    private readonly PlatformSettings settings;
    private readonly IClock clock;
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly Lock gate = new();
    private readonly Dictionary<PlatformTier, KubernetesApi> clusters = [];
    private OctopusApi? octopus;
    private CodefreshApi? codefresh;
    private AzureApi? azure;
    private GitHubApi? gitHub;
    private TokenCredential? azureCredential;

    /// <summary>Creates the factory.</summary>
    /// <param name="settings">Harness settings and secrets.</param>
    /// <param name="clock">Clock for waits; the system clock when omitted.</param>
    /// <param name="handlerFactory">Creates the HTTP message handler of each REST client (unit tests pass stubs); real sockets when omitted.</param>
    public PlatformClients(PlatformSettings settings, IClock? clock = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        this.settings = settings;
        this.clock = clock ?? SystemClock.Instance;
        this.handlerFactory = handlerFactory;
    }

    /// <summary>The Octopus client; needs <c>OctopusUrl</c>, <c>OctopusSpaceId</c> and <c>OCTOPUS_API_KEY</c>.</summary>
    /// <exception cref="PlatformPrerequisiteException">A prerequisite is missing (the test becomes Inconclusive).</exception>
    public IOctopusApi Octopus
    {
        get
        {
            lock (gate)
            {
                return octopus ??= CreateOctopus();
            }
        }
    }

    /// <summary>The Codefresh client; needs <c>CODEFRESH_API_KEY</c>.</summary>
    /// <exception cref="PlatformPrerequisiteException">A prerequisite is missing (the test becomes Inconclusive).</exception>
    public ICodefreshApi Codefresh
    {
        get
        {
            lock (gate)
            {
                return codefresh ??= CreateCodefresh();
            }
        }
    }

    /// <summary>
    /// The Azure client; needs the subscription (<c>AZURE_SUBSCRIPTION_ID</c> or <c>AzureSubscriptionId</c>) and a credential:
    /// the service principal in <c>AZURE_CLIENT_ID</c>, <c>AZURE_CLIENT_SECRET</c> and <c>AZURE_TENANT_ID</c>, or whatever
    /// <c>DefaultAzureCredential</c> finds (its absence surfaces as Inconclusive on the first call).
    /// </summary>
    /// <exception cref="PlatformPrerequisiteException">A prerequisite is missing (the test becomes Inconclusive).</exception>
    public IAzureApi Azure
    {
        get
        {
            lock (gate)
            {
                return azure ??= CreateAzure();
            }
        }
    }

    /// <summary>The GitHub client; needs <c>GITHUB_TOKEN</c>.</summary>
    /// <exception cref="PlatformPrerequisiteException">A prerequisite is missing (the test becomes Inconclusive).</exception>
    public IGitHubApi GitHub
    {
        get
        {
            lock (gate)
            {
                return gitHub ??= CreateGitHub();
            }
        }
    }

    /// <summary>
    /// The Kubernetes client of a tier's cluster, connected through ARM and authenticated with Entra; needs the Azure
    /// prerequisites plus <c>Tiers.&lt;tier&gt;.ResourceGroup</c> and <c>Tiers.&lt;tier&gt;.ClusterName</c>.
    /// </summary>
    /// <param name="tier">The tier whose cluster to reach.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    /// <exception cref="PlatformPrerequisiteException">A prerequisite is missing (the test becomes Inconclusive).</exception>
    public async Task<IKubernetesApi> KubernetesAsync(PlatformTier tier, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (clusters.TryGetValue(tier, out var existing))
            {
                return existing;
            }
        }

        var tierSettings = settings.Tier(tier);
        var key = tier.ToKey();
        var check = settings.Check($"the Kubernetes API of the {key} cluster");
        AddAzurePrerequisites(check);
        check.Setting($"Tiers.{key}.ResourceGroup", tierSettings.ResourceGroup)
            .Setting($"Tiers.{key}.ClusterName", tierSettings.ClusterName)
            .ThrowIfMissing();
        var azureApi = Azure;
        var credential = AzureCredential();
        var connected = await KubernetesApi.ConnectAsync(azureApi, new EntraTokenProvider(credential), tierSettings.ResourceGroup!, tierSettings.ClusterName!, settings.TlsSystemTrust, cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (clusters.TryGetValue(tier, out var raced))
            {
                connected.Dispose();
                return raced;
            }

            clusters[tier] = connected;
            return connected;
        }
    }

    /// <summary>Disposes every client created so far.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            octopus?.Dispose();
            codefresh?.Dispose();
            azure?.Dispose();
            gitHub?.Dispose();
            foreach (var cluster in clusters.Values)
            {
                cluster.Dispose();
            }

            clusters.Clear();
        }
    }

    private OctopusApi CreateOctopus()
    {
        settings.Check("the Octopus API")
            .Setting(nameof(PlatformSettings.OctopusUrl), settings.OctopusUrl)
            .Setting(nameof(PlatformSettings.OctopusSpaceId), settings.OctopusSpaceId)
            .Secret(EnvironmentVariableNames.OctopusApiKey, settings.Secrets.OctopusApiKey)
            .ThrowIfMissing();
        return OctopusApi.Create(settings.OctopusUrl!, settings.OctopusSpaceId!, settings.Secrets.OctopusApiKey!, settings.TimeLimits.HttpTimeout, clock, settings.TimeLimits.PollInterval, handlerFactory?.Invoke());
    }

    private CodefreshApi CreateCodefresh()
    {
        settings.Check("the Codefresh API")
            .Setting(nameof(PlatformSettings.CodefreshUrl), settings.CodefreshUrl)
            .Secret(EnvironmentVariableNames.CodefreshApiKey, settings.Secrets.CodefreshApiKey)
            .ThrowIfMissing();
        return CodefreshApi.Create(settings.CodefreshUrl, settings.Secrets.CodefreshApiKey!, settings.TimeLimits.HttpTimeout, clock, settings.TimeLimits.PollInterval, handlerFactory?.Invoke());
    }

    private AzureApi CreateAzure()
    {
        var check = settings.Check("the Azure API");
        AddAzurePrerequisites(check);
        check.ThrowIfMissing();
        return new AzureApi(
            AzureCredential(),
            new AzureApiOptions
            {
                SubscriptionId = settings.AzureSubscriptionId!,
                TenantId = settings.AzureTenantId,
                RegistryLoginServer = settings.RegistryLoginServer,
                HttpTimeout = settings.TimeLimits.HttpTimeout,
            },
            handlerFactory?.Invoke());
    }

    private GitHubApi CreateGitHub()
    {
        settings.Check("the GitHub API")
            .Secret(EnvironmentVariableNames.GitHubToken, settings.Secrets.GitHubToken)
            .ThrowIfMissing();
        return GitHubApi.Create(settings.Secrets.GitHubToken!, settings.TimeLimits.HttpTimeout, handlerFactory?.Invoke());
    }

    private void AddAzurePrerequisites(PrerequisiteCheck check) =>
        check.Setting($"{nameof(PlatformSettings.AzureSubscriptionId)} (or {EnvironmentVariableNames.AzureSubscriptionId})", settings.AzureSubscriptionId);

    private TokenCredential AzureCredential()
    {
        lock (gate)
        {
            return azureCredential ??= AzureCredentialFactory.Create(settings);
        }
    }
}
