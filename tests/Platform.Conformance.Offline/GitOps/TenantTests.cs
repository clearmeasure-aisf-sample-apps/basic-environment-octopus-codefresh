using System.Text.Json;
using Platform.Conformance.Harness;
using Platform.Conformance.Tests.GitOps;

namespace Platform.Conformance.Offline.GitOps;

/// <summary>
/// CAP-GIT-002, offline part: the tenant chart renders every committed descriptor into exactly its tenant on each tier
/// (§7.0 Tenant), checked against names derived independently from the descriptor (<see cref="GitOpsApp"/>): AppProject,
/// namespaces, quota, limit range, NetworkPolicies, ClusterSecretStores, Applications with the Octopus scope of their own
/// project, the image policy, the database volume, the backups and the ListenerSets. The live part
/// (Platform.Conformance.Tests.GitOps.TenantTests) finds the same objects on the clusters.
/// </summary>
[TestFixture]
[Category(Categories.Offline)]
public class TenantTests
{
    private static readonly Dictionary<string, string> StandInDomain = new(StringComparer.Ordinal) { ["platform.appsDomain"] = TenantChart.StandInDomain };

    [Test]
    [Capability("CAP-GIT-002")]
    public void Should_Render_EachDescriptorOnEachTier_YieldsExactlyItsTenant()
    {
        var descriptors = TenantChart.Descriptors;

        descriptors.ShouldNotBeEmpty("apps/*.yaml holds no descriptor");
        TenantChart.EnsureHelmAvailable();
        Assert.Multiple(() =>
        {
            foreach (var (app, path) in descriptors)
            {
                foreach (var tier in TenantChart.Tiers)
                {
                    var render = TenantChart.Render(path, tier, StandInDomain);
                    render.ExitCode.ShouldBe(0, $"{app.Name} on {tier}: {render.Error}");
                    var objects = render.Objects;
                    var rendered = string.Join(Environment.NewLine, objects.Select(TenantChart.Identity).Order(StringComparer.Ordinal));
                    rendered.ShouldBe(string.Join(Environment.NewLine, Expected(app, tier).Order(StringComparer.Ordinal)), $"{app.Name} on {tier}");
                    objects.Where(item => GitOpsCluster.Text(item, "metadata", "labels", "platform/app") != app.Name || GitOpsCluster.Text(item, "metadata", "labels", "app.kubernetes.io/managed-by") != "platform-tenant")
                        .Select(TenantChart.Identity)
                        .ShouldBeEmpty($"{app.Name} on {tier}: objects without the tenant labels");
                }
            }
        });
    }

    [Test]
    [Capability("CAP-GIT-002")]
    public void Should_Render_Applications_CarryOnlyTheirOwnProjectScope()
    {
        var descriptors = TenantChart.Descriptors;

        TenantChart.EnsureHelmAvailable();

        Assert.Multiple(() =>
        {
            foreach (var (app, path) in descriptors)
            {
                foreach (var tier in TenantChart.Tiers)
                {
                    var values = GitOpsRepository.TenantValues(tier);
                    var applications = TenantChart.Render(path, tier).Objects.Where(item => GitOpsCluster.Text(item, "kind") == "Application").ToArray();
                    foreach (var environment in app.EnvironmentsOf(tier))
                    {
                        if (app.HasDatabase)
                        {
                            var database = Single(applications, app.DatabaseApplication(environment));
                            AssertApplication(database, app, app.Namespace(environment), $"gitops/apps/{app.Name}/envs/{environment}/db", values.EnvRepoUrl);
                            Annotations(database).Keys.Where(key => key.StartsWith("argo.octopus.com/", StringComparison.Ordinal))
                                .ShouldBeEmpty($"{app.DatabaseApplication(environment)} must carry no Octopus scope: Octopus never pins the database");
                        }

                        foreach (var deployable in app.Deployables)
                        {
                            var workload = Single(applications, app.Application(deployable, environment));
                            var expectedPath = deployable.Packaging == "helm" ? null : $"gitops/apps/{app.Name}/envs/{environment}/{deployable.Name}";
                            AssertApplication(workload, app, app.NamespaceOf(deployable, environment), expectedPath, values.EnvRepoUrl);
                            var annotations = Annotations(workload);
                            annotations.GetValueOrDefault("argo.octopus.com/project").ShouldBe(deployable.OctopusProject, app.Application(deployable, environment));
                            app.OctopusProjects.ShouldContain(deployable.OctopusProject, $"{app.Application(deployable, environment)} is scoped to a project the app does not declare");
                            annotations.GetValueOrDefault("argo.octopus.com/environment").ShouldBe(environment, app.Application(deployable, environment));
                        }
                    }
                }
            }
        });
    }

    [Test]
    [Capability("CAP-GIT-002")]
    public void Should_Render_ClusterSecretStores_AdmitOnlyTheAppEnvironmentAndReadItsVault()
    {
        const string subscription = "0A1B2C3D-4E5F-4A6B-8C7D-9E0F1A2B3C4D";
        var descriptors = TenantChart.Descriptors;

        TenantChart.EnsureHelmAvailable();

        Assert.Multiple(() =>
        {
            foreach (var (app, path) in descriptors)
            {
                foreach (var tier in TenantChart.Tiers)
                {
                    var stores = TenantChart.Render(path, tier, new Dictionary<string, string> { ["platform.subscriptionId"] = subscription }).Objects
                        .Where(item => GitOpsCluster.Text(item, "kind") == "ClusterSecretStore")
                        .ToArray();
                    stores.Length.ShouldBe(app.EnvironmentsOf(tier).Count, $"{app.Name} on {tier}: one store per environment");
                    foreach (var environment in app.EnvironmentsOf(tier))
                    {
                        var store = Single(stores, app.Store(environment));
                        var admitted = GitOpsCluster.Child(store, "spec", "conditions").EnumerateArray()
                            .SelectMany(condition => GitOpsCluster.Child(condition, "namespaces").EnumerateArray().Select(item => item.GetString() ?? string.Empty))
                            .Order(StringComparer.Ordinal);
                        string.Join(", ", admitted).ShouldBe(string.Join(", ", app.Namespaces(environment).Append(GitOpsNames.BackupNamespace).Order(StringComparer.Ordinal)), $"namespaces admitted by {app.Store(environment)}");
                        GitOpsCluster.Text(store, "spec", "provider", "azurekv", "vaultUrl")
                            .ShouldBe($"https://{GitOpsNames.VaultName(subscription, app.Name, environment)}.vault.azure.net", $"vault of {app.Store(environment)} (decision 8: lowercase subscription)");
                    }
                }
            }
        });
    }

    private static IEnumerable<string> Expected(GitOpsApp app, string tier)
    {
        yield return $"AppProject {GitOpsNames.ArgoNamespace}/{app.AppProject}";
        if (app.Previews && tier == "nonprod")
        {
            yield return $"AppProject {GitOpsNames.ArgoNamespace}/{app.AppProject}-previews";
        }

        yield return $"ImageValidatingPolicy -/{app.SignerPolicy}";
        foreach (var environment in app.EnvironmentsOf(tier))
        {
            foreach (var namespaceName in app.Namespaces(environment))
            {
                yield return $"Namespace -/{namespaceName}";
                yield return $"ResourceQuota {namespaceName}/tenant";
                yield return $"LimitRange {namespaceName}/tenant";
                yield return $"ListenerSet {GitOpsNames.IngressNamespace}/{namespaceName}";
                foreach (var policy in GitOpsNames.NetworkPolicies)
                {
                    yield return $"NetworkPolicy {namespaceName}/{policy}";
                }
            }

            yield return $"ClusterSecretStore -/{app.Store(environment)}";
            foreach (var deployable in app.Deployables)
            {
                yield return $"Application {GitOpsNames.ArgoNamespace}/{app.Application(deployable, environment)}";
            }

            if (!app.HasDatabase)
            {
                continue;
            }

            yield return $"NetworkPolicy {app.Namespace(environment)}/{GitOpsNames.DatabaseNetworkPolicy}";
            yield return $"Application {GitOpsNames.ArgoNamespace}/{app.DatabaseApplication(environment)}";
            yield return $"PersistentVolume -/{app.Disk(environment)}";
            if (GitOpsNames.BackupEnvironments.Contains(environment, StringComparer.Ordinal))
            {
                yield return $"CronJob {GitOpsNames.BackupNamespace}/db-backup-{app.Name}-{environment}";
                yield return $"CronJob {GitOpsNames.BackupNamespace}/db-restore-{app.Name}-{environment}";
                yield return $"ExternalSecret {GitOpsNames.BackupNamespace}/db-sa-{app.Name}-{environment}";
            }
        }
    }

    private static void AssertApplication(JsonElement application, GitOpsApp app, string destination, string? path, string envRepoUrl)
    {
        var name = GitOpsCluster.Text(application, "metadata", "name");
        GitOpsCluster.Text(application, "spec", "project").ShouldBe(app.AppProject, $"project of {name}");
        GitOpsCluster.Text(application, "spec", "destination", "namespace").ShouldBe(destination, $"destination of {name}");
        GitOpsCluster.Text(application, "spec", "destination", "server").ShouldBe("https://kubernetes.default.svc", $"destination server of {name}");
        GitOpsCluster.Text(application, "spec", "source", "repoURL").ShouldBe(envRepoUrl, $"source of {name}");
        if (path is not null)
        {
            GitOpsCluster.Text(application, "spec", "source", "path").ShouldBe(path, $"path of {name}");
        }
    }

    private static JsonElement Single(IEnumerable<JsonElement> items, string name)
    {
        var matches = items.Where(item => GitOpsCluster.Text(item, "metadata", "name") == name).ToArray();
        matches.Length.ShouldBe(1, $"{name} rendered {matches.Length} times");
        return matches[0];
    }

    private static Dictionary<string, string> Annotations(JsonElement item)
    {
        var annotations = GitOpsCluster.Child(item, "metadata", "annotations");
        return annotations.ValueKind == JsonValueKind.Object
            ? annotations.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal)
            : [];
    }
}
