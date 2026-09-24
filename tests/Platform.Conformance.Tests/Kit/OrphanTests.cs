using Platform.Conformance.Harness;
using Platform.Conformance.Harness.Settings;
using Platform.Conformance.Harness.Support;

namespace Platform.Conformance.Tests.Kit;

/// <summary>
/// CAP-KIT-008: no orphaned platform resource exists: database disks, app vaults, app resource groups, app namespaces,
/// registry repositories and Octopus and Codefresh projects all belong to a descriptor. The foreign resource groups
/// NetworkWatcherRG and ai-model are never read.
/// </summary>
[TestFixture]
[Category(Categories.Live)]
public class OrphanTests : PlatformTestBase
{
    [Test]
    [Capability("CAP-KIT-008")]
    [CancelAfter(900_000)]
    public async Task Should_PlatformResources_WithoutDescriptor_DoNotExist()
    {
        var cancellationToken = TestContext.CurrentContext.CancellationToken;
        Settings.Check("the orphan report")
            .Setting(nameof(Settings.OctopusUrl), Settings.OctopusUrl)
            .Setting(nameof(Settings.OctopusSpaceId), Settings.OctopusSpaceId)
            .Setting(nameof(Settings.AzureSubscriptionId), Settings.AzureSubscriptionId)
            .Setting(nameof(Settings.RegistryLoginServer), Settings.RegistryLoginServer)
            .Secret(EnvironmentVariableNames.OctopusApiKey, Settings.Secrets.OctopusApiKey)
            .Secret(EnvironmentVariableNames.CodefreshApiKey, Settings.Secrets.CodefreshApiKey)
            .ThrowIfMissing();
        var descriptors = KitDescriptors.Load(RepositoryRoot.Find(AppContext.BaseDirectory, ProcessEnvironmentVariables.Instance)).ToDictionary(app => app.Name, StringComparer.Ordinal);
        using var rest = new KitRest(Settings);
        var orphans = new List<string>();

        foreach (var tier in new[] { "nonprod", "prod" })
        {
            foreach (var disk in await Azure.ListManagedDisksAsync($"rg-platform-{tier}-data", cancellationToken))
            {
                var app = AppOf(disk.Name, "disk-");
                if (app is not null && !(descriptors.TryGetValue(app, out var owner) && owner.HasDatabase))
                {
                    orphans.Add($"disk {disk.Name}");
                }
                else if (app is null && disk.Tags.TryGetValue("kubernetes.io-created-for-pvc-namespace", out var ns) && !descriptors.ContainsKey(ns.Split('-')[0]))
                {
                    orphans.Add($"disk {disk.Name} (claim in {ns})");
                }
            }

            orphans.AddRange((await rest.ArmNamesAsync($"rg-platform-{tier}-apps", "Microsoft.KeyVault/vaults", "2023-07-01", cancellationToken))
                .Where(vault => AppOf(vault, "kv-") is not { } app || !descriptors.ContainsKey(app))
                .Select(vault => $"vault {vault}"));
            var kubernetes = await KubernetesAsync(tier == "prod" ? PlatformTier.Prod : PlatformTier.NonProd, cancellationToken);
            orphans.AddRange((await kubernetes.ListNamespacesAsync("tier=app", cancellationToken))
                .Where(ns => !ns.Labels.TryGetValue("platform/app", out var app) || !descriptors.ContainsKey(app))
                .Select(ns => $"namespace {ns.Name} on {kubernetes.ClusterName}"));
        }

        orphans.AddRange((await Azure.ListResourceGroupsAsync(cancellationToken))
            .Select(group => group.Name)
            .Where(name => name.StartsWith("rg-app-", StringComparison.Ordinal))
            .Where(name => AppOf(name, "rg-app-") is not { } app || !(descriptors.TryGetValue(app, out var owner) && owner.HasResourceGroup))
            .Select(name => $"resource group {name}"));
        orphans.AddRange((await rest.RegistryRepositoriesAsync(cancellationToken))
            .Where(repository => repository.StartsWith("apps/", StringComparison.Ordinal) || repository.StartsWith("apps-previews/", StringComparison.Ordinal))
            .Where(repository => repository != KitNames.FixtureUnsignedRepository && !descriptors.ContainsKey(repository.Split('/')[1]))
            .Select(repository => $"registry repository {repository}"));
        var declaredOctopus = descriptors.Values.SelectMany(app => app.OctopusProjects).Concat(KitNames.PlatformOctopusProjects).ToHashSet(StringComparer.Ordinal);
        orphans.AddRange((await rest.OctopusAllAsync("projects", cancellationToken))
            .Select(project => project.GetProperty("Name").GetString()!)
            .Where(name => !declaredOctopus.Contains(name))
            .Select(name => $"Octopus project {name}"));
        var declaredCodefresh = descriptors.Values.SelectMany(app => app.CodefreshProjects).Append(KitNames.PlatformCodefreshProject).ToHashSet(StringComparer.Ordinal);
        orphans.AddRange((await rest.CodefreshProjectNamesAsync(cancellationToken))
            .Where(name => !declaredCodefresh.Contains(name))
            .Select(name => $"Codefresh project {name}"));

        AttachArtifact("kit-orphans.txt", string.Join(Environment.NewLine, orphans));
        orphans.ShouldBeEmpty("resources without a descriptor; retire them in the order of docs/onboarding.md, section Retire");
    }

    private static string? AppOf(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = name[prefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 ? rest[..dash] : null;
    }
}
