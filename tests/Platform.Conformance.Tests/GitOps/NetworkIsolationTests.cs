using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>
/// CAP-GIT-003: cross-app traffic is blocked. A probe pod in <c>sandbox-tdd</c> opens TCP connections: to its own
/// database (allowed by <c>platform-allow-same-app</c>), and to Services of <c>workorders-tdd</c> and of the sandbox's
/// other environment (refused by the tenant NetworkPolicies of those namespaces). The probe runs the SQL Server image of
/// the sandbox database (an image the namespace may run; bash <c>/dev/tcp</c> needs no extra tool).
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class NetworkIsolationTests : GitOpsTestBase
{
    private const string Namespace = "sandbox-tdd";
    private const string Open = "open";
    private const string Closed = "closed";

    [Test]
    [Capability("CAP-GIT-003")]
    [Category(Categories.NonProd)]
    [CancelAfter(10 * 60 * 1000)]
    public async Task Should_Connect_FromSandboxTdd_ReachesOwnDatabaseOnlyAndNotOtherApps()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        var kubernetes = await KubernetesAsync(PlatformTier.NonProd, cancellationToken);
        var cluster = await ClusterAsync(PlatformTier.NonProd, cancellationToken);
        var ownDatabase = (await kubernetes.ListStatefulSetsAsync(Namespace, cancellationToken: cancellationToken)).FirstOrDefault(set => set.Name == "db");
        if (ownDatabase is not { ReadyReplicas: > 0 } || ownDatabase.Images.Count == 0)
        {
            Unobservable($"StatefulSet {Namespace}/db is not ready; the allowed path cannot be shown");
        }

        var targets = new List<(string Host, int Port, string Expected)> { ($"db.{Namespace}.svc.cluster.local", 1433, Open) };
        if ((await kubernetes.ListDeploymentsAsync("workorders-tdd", cancellationToken: cancellationToken)).Any(deployment => deployment is { Name: "ui-server", ReadyReplicas: > 0 }))
        {
            targets.Add(("ui-server.workorders-tdd.svc.cluster.local", 8080, Closed));
        }

        if ((await kubernetes.ListStatefulSetsAsync("workorders-tdd", cancellationToken: cancellationToken)).Any(set => set is { Name: "db", ReadyReplicas: > 0 }))
        {
            targets.Add(("db.workorders-tdd.svc.cluster.local", 1433, Closed));
        }

        if ((await kubernetes.ListStatefulSetsAsync("sandbox-uat", cancellationToken: cancellationToken)).Any(set => set is { Name: "db", ReadyReplicas: > 0 }))
        {
            targets.Add(("db.sandbox-uat.svc.cluster.local", 1433, Closed));
        }

        if (targets.Count(target => target.Expected == Closed) == 0)
        {
            Unobservable("no Service of another app or environment is running on nonprod (workorders-tdd, sandbox-uat); nothing to block");
        }

        var script = "probe() { if timeout 5 bash -c \"exec 3<>/dev/tcp/$1/$2\" 2>/dev/null; then echo \"open $1:$2\"; else echo \"closed $1:$2\"; fi; }; "
            + string.Join("; ", targets.Select(target => $"probe {target.Host} {target.Port}"));
        var pod = ProbePod(
            Namespace,
            Run.ResourceName("netprobe"),
            ownDatabase.Images[0],
            FixtureLabels,
            ["/bin/bash", "-c", script],
            new Dictionary<string, string> { ["MSSQL_PID"] = "Express" });

        var result = await cluster.RunProbePodAsync(pod, TimeSpan.FromMinutes(4), cancellationToken);

        AttachArtifact($"{pod.Metadata.Name}.log", result.Log);
        result.Phase.ShouldBe("Succeeded", result.Log);
        var observed = result.Log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        Assert.Multiple(() =>
        {
            foreach (var (host, port, expected) in targets)
            {
                observed.GetValueOrDefault($"{host}:{port}").ShouldBe(expected, $"TCP {host}:{port} from {Namespace}");
            }
        });
    }
}
