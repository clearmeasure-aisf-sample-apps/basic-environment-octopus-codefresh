using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Kit;

/// <summary>
/// CAP-KIT-003: every descriptor's live objects exist and match: the Octopus group and projects (disabled when the app
/// is frozen), the Codefresh projects, the tenant Application and namespaces on each cluster, the app vaults (by the
/// same formula Terraform and the tenant chart use) and the database disks.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class LiveInventoryTests : PlatformTestBase
{
    [Test]
    [Capability("CAP-KIT-003")]
    [CancelAfter(900_000)]
    public async Task Should_LiveObjects_EveryDescriptor_ExistAndMatch()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        Settings.Check("the live inventory of the descriptors")
            .Setting(nameof(Settings.OctopusUrl), Settings.OctopusUrl)
            .Setting(nameof(Settings.OctopusSpaceId), Settings.OctopusSpaceId)
            .Setting(nameof(Settings.AzureSubscriptionId), Settings.AzureSubscriptionId)
            .Secret(EnvironmentVariableNames.OctopusApiKey, Settings.Secrets.OctopusApiKey)
            .Secret(EnvironmentVariableNames.CodefreshApiKey, Settings.Secrets.CodefreshApiKey)
            .ThrowIfMissing();
        var descriptors = KitDescriptors.Load(RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance));
        using var rest = new KitRest(Settings);
        var groups = (await rest.OctopusAllAsync("projectgroups", cancellationToken)).ToDictionary(group => group.GetProperty("Name").GetString()!, group => group.GetProperty("Id").GetString()!);
        var projects = (await rest.OctopusAllAsync("projects", cancellationToken)).ToDictionary(project => project.GetProperty("Name").GetString()!, project => project);
        var clusters = new Dictionary<string, IKubernetesApi>
        {
            ["nonprod"] = await KubernetesAsync(PlatformTier.NonProd, cancellationToken),
            ["prod"] = await KubernetesAsync(PlatformTier.Prod, cancellationToken),
        };
        var missing = new List<string>();

        foreach (var app in descriptors)
        {
            if (!groups.TryGetValue($"app-{app.Name}", out var groupId))
            {
                missing.Add($"Octopus project group app-{app.Name}");
            }

            foreach (var name in app.OctopusProjects)
            {
                if (!projects.TryGetValue(name, out var project))
                {
                    missing.Add($"Octopus project {name}");
                    continue;
                }

                if (groupId is not null && project.GetProperty("ProjectGroupId").GetString() != groupId)
                {
                    missing.Add($"Octopus project {name} in group app-{app.Name}");
                }

                if (project.GetProperty("IsDisabled").GetBoolean() != (app.Status == "frozen"))
                {
                    missing.Add($"Octopus project {name} {(app.Status == "frozen" ? "disabled" : "enabled")} ({app.Status})");
                }
            }

            foreach (var name in app.CodefreshProjects)
            {
                if (!await rest.CodefreshProjectExistsAsync(name, cancellationToken))
                {
                    missing.Add($"Codefresh project {name}");
                }
            }

            foreach (var (tier, cluster) in clusters)
            {
                var environments = app.EnvironmentsOf(tier).ToArray();
                if (environments.Length == 0)
                {
                    continue;
                }

                if (await cluster.GetCustomObjectAsync(CustomResourceKind.ArgoApplication, "argocd", $"tenant-{app.Name}", cancellationToken) is null)
                {
                    missing.Add($"Application tenant-{app.Name} on {cluster.ClusterName}");
                }

                var namespaces = (await cluster.ListNamespacesAsync($"platform/app={app.Name}", cancellationToken)).Select(ns => ns.Name).ToHashSet(StringComparer.Ordinal);
                missing.AddRange(environments.Where(environment => !namespaces.Contains($"{app.Name}-{environment}")).Select(environment => $"namespace {app.Name}-{environment} on {cluster.ClusterName}"));
                var vaults = await rest.ArmNamesAsync($"rg-platform-{tier}-apps", "Microsoft.KeyVault/vaults", "2023-07-01", cancellationToken);
                missing.AddRange(environments.Select(environment => KitNames.VaultName(Settings.AzureSubscriptionId!, app.Name, environment))
                    .Where(vault => !vaults.Contains(vault, StringComparer.OrdinalIgnoreCase)).Select(vault => $"vault {vault} in rg-platform-{tier}-apps"));
                if (app.HasDatabase)
                {
                    var disks = (await Azure.ListManagedDisksAsync($"rg-platform-{tier}-data", cancellationToken)).Select(disk => disk.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    missing.AddRange(environments.Where(environment => !disks.Contains($"disk-{app.Name}-{environment}-db")).Select(environment => $"disk disk-{app.Name}-{environment}-db"));
                }
            }
        }

        AttachArtifact("kit-live-inventory.txt", string.Join(Environment.NewLine, missing));
        missing.ShouldBeEmpty($"{descriptors.Count} descriptor(s); missing or mismatched live objects");
    }
}
