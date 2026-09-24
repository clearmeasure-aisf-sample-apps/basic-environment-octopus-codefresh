using NUnit.Framework;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Harness;

/// <summary>
/// Base fixture for live conformance tests. It loads <see cref="PlatformSettings"/>, exposes the platform APIs, turns a
/// missing secret or setting into an <b>Inconclusive</b> result, and runs registered cleanup actions in reverse order in
/// <c>[OneTimeTearDown]</c>, even when a test failed.
/// </summary>
/// <example>
/// <code>
/// [Category(Categories.Live)]
/// public class SleepTests : PlatformTestBase
/// {
///     [Test, Capability("CAP-SLEEP-001"), Category(Categories.NonProd)]
///     public async Task WhenGetClusterStateAsync_OutsideWorkingWindow_ClusterIsStopped()
///     {
///         var tier = Settings.Tier(PlatformTier.NonProd);
///         var state = await Azure.GetClusterStateAsync(tier.ResourceGroup!, tier.ClusterName!);
///         state.PowerState.ShouldBe("Stopped");
///     }
/// }
/// </code>
/// </example>
public abstract class PlatformTestBase
{
    private readonly CleanupRegistry cleanup = new();
    private PlatformClients? clients;

    /// <summary>Settings and secrets of the run; loaded in <see cref="LoadPlatformSettings"/>.</summary>
    protected PlatformSettings Settings { get; private set; } = null!;

    /// <summary>Run ID, start time and artifacts folder, shared by every fixture of the process.</summary>
    protected TestRunContext Run => TestRunContext.Current;

    /// <summary>Cleanup actions of this fixture; they run in reverse order after its last test.</summary>
    protected ICleanupRegistry Cleanup => cleanup;

    /// <summary>Octopus Deploy; Inconclusive when <c>OCTOPUS_API_KEY</c>, <c>OctopusUrl</c> or <c>OctopusSpaceId</c> is missing.</summary>
    protected IOctopusApi Octopus => Clients.Octopus;

    /// <summary>Codefresh; Inconclusive when <c>CODEFRESH_API_KEY</c> is missing.</summary>
    protected ICodefreshApi Codefresh => Clients.Codefresh;

    /// <summary>Azure Resource Manager and the registry; Inconclusive when the subscription or a credential is missing.</summary>
    protected IAzureApi Azure => Clients.Azure;

    /// <summary>GitHub; Inconclusive when <c>GITHUB_TOKEN</c> is missing.</summary>
    protected IGitHubApi GitHub => Clients.GitHub;

    private PlatformClients Clients => clients ?? throw new InvalidOperationException($"{nameof(PlatformTestBase)} is not set up: its [OneTimeSetUp] did not run.");

    /// <summary>Connects to the cluster of <paramref name="tier"/>; Inconclusive when Azure prerequisites or the tier's cluster settings are missing.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    protected Task<IKubernetesApi> KubernetesAsync(PlatformTier tier, CancellationToken cancellationToken = default) => Clients.KubernetesAsync(tier, cancellationToken);

    /// <summary>Loads settings and prepares the clients. A malformed settings file fails the fixture; missing values do not.</summary>
    [OneTimeSetUp]
    public void LoadPlatformSettings()
    {
        Settings = PlatformSettings.Load();
        clients = new PlatformClients(Settings);
    }

    /// <summary>Runs every registered cleanup action in reverse order, disposes the clients, then reports failed cleanups.</summary>
    /// <exception cref="CleanupFailedException">One or more cleanup actions threw.</exception>
    [OneTimeTearDown]
    public async Task RunCleanupAsync()
    {
        var failures = await cleanup.RunAllAsync().ConfigureAwait(false);
        clients?.Dispose();
        clients = null;
        if (failures.Count > 0)
        {
            throw new CleanupFailedException(failures);
        }
    }

    /// <summary>Writes a text artifact of this run and attaches it to the current test result.</summary>
    /// <param name="fileName">Plain file name, for example <c>env-wake-task.log</c>.</param>
    /// <param name="content">Text to write.</param>
    /// <returns>The full path of the artifact.</returns>
    protected string AttachArtifact(string fileName, string content)
    {
        var path = Run.WriteArtifact(fileName, content);
        TestContext.AddTestAttachment(path);
        return path;
    }
}
