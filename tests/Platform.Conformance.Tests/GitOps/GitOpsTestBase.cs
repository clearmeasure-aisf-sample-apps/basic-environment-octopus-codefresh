using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using k8s.Models;
using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// Base fixture of the GitOps capability tests (CAP-GIT): the harness base plus a full Kubernetes client per tier
/// (<see cref="GitOpsCluster"/>), the raw REST helper and the descriptors of the checked-out environment repository.
/// Clients are disposed after the fixture's other cleanup actions.
/// </summary>
public abstract class GitOpsTestBase : PlatformTestBase
{
    private readonly Dictionary<PlatformTier, GitOpsCluster> clusters = [];
    private GitOpsRest? rest;

    /// <summary>Raw REST calls (Key Vault, GitHub, Octopus, app hosts).</summary>
    protected GitOpsRest Rest
    {
        get
        {
            if (rest is null)
            {
                rest = new GitOpsRest(Settings);
                var created = rest;
                Cleanup.Register("dispose the GitOps REST helper", _ =>
                {
                    created.Dispose();
                    rest = null;
                    return Task.CompletedTask;
                });
            }

            return rest;
        }
    }

    /// <summary>The full Kubernetes client of a tier's cluster; Inconclusive when its settings or credential are missing.</summary>
    /// <param name="tier">The tier.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    protected async Task<GitOpsCluster> ClusterAsync(PlatformTier tier, CancellationToken cancellationToken)
    {
        if (clusters.TryGetValue(tier, out var existing))
        {
            return existing;
        }

        var cluster = await GitOpsCluster.ConnectAsync(Settings, Azure, tier, cancellationToken).ConfigureAwait(false);
        clusters[tier] = cluster;
        Cleanup.Register($"dispose the GitOps Kubernetes client of {cluster.ClusterName}", _ =>
        {
            cluster.Dispose();
            clusters.Remove(tier);
            return Task.CompletedTask;
        });
        return cluster;
    }

    /// <summary>Labels of a Kubernetes fixture of this run.</summary>
    protected Dictionary<string, string> FixtureLabels => GitOpsCluster.FixtureLabels(Run.RunId);

    /// <summary>The fixture labels as a JSON object, for custom objects.</summary>
    protected JsonObject FixtureLabelsJson()
    {
        var labels = new JsonObject();
        foreach (var (key, value) in FixtureLabels)
        {
            labels[key] = value;
        }

        return labels;
    }

    /// <summary>One descriptor of the checked-out repository.</summary>
    /// <param name="name">App slug.</param>
    protected static GitOpsApp App(string name) => GitOpsRepository.LoadApp(name);

    /// <summary>Every descriptor of the checked-out repository.</summary>
    protected static IReadOnlyList<GitOpsApp> Apps() => GitOpsRepository.LoadApps();

    /// <summary>The tenant chart's platform values of a tier.</summary>
    /// <param name="tier">The tier.</param>
    protected static TenantPlatformValues TenantValues(PlatformTier tier) => GitOpsRepository.TenantValues(GitOpsNames.Key(tier));

    /// <summary>Ends the test as Inconclusive: what it observes is not in place yet (never a pass or a failure).</summary>
    /// <param name="reason">What is missing.</param>
    [DoesNotReturn]
    protected static void Unobservable(string reason) => throw new InconclusiveException(reason);

    /// <summary>
    /// A bare probe pod that passes Pod Security "restricted" and the workload baseline: non-root, no privilege
    /// escalation, every capability dropped, a read-only root file system, requests and a memory limit.
    /// </summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Pod name.</param>
    /// <param name="image">Image, pinned by tag or digest and allowed in the namespace.</param>
    /// <param name="labels">Labels.</param>
    /// <param name="command">Command and arguments.</param>
    /// <param name="environment">Environment variables.</param>
    protected static V1Pod ProbePod(string namespaceName, string name, string image, IDictionary<string, string> labels, IList<string> command, IDictionary<string, string>? environment = null) => new()
    {
        ApiVersion = "v1",
        Kind = "Pod",
        Metadata = new V1ObjectMeta { Name = name, NamespaceProperty = namespaceName, Labels = labels },
        Spec = new V1PodSpec
        {
            RestartPolicy = "Never",
            AutomountServiceAccountToken = false,
            ActiveDeadlineSeconds = 300,
            SecurityContext = new V1PodSecurityContext
            {
                RunAsNonRoot = true,
                RunAsUser = 10001,
                SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" },
            },
            Containers =
            [
                new V1Container
                {
                    Name = "probe",
                    Image = image,
                    Command = command,
                    Env = environment?.Select(pair => new V1EnvVar { Name = pair.Key, Value = pair.Value }).ToList(),
                    Resources = new V1ResourceRequirements
                    {
                        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("10m"), ["memory"] = new("32Mi") },
                        Limits = new Dictionary<string, ResourceQuantity> { ["memory"] = new("64Mi") },
                    },
                    SecurityContext = new V1SecurityContext
                    {
                        AllowPrivilegeEscalation = false,
                        ReadOnlyRootFilesystem = true,
                        Capabilities = new V1Capabilities { Drop = ["ALL"] },
                    },
                },
            ],
        },
    };
}
