using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Platform.Onboarding.Descriptors;
using Platform.Onboarding.Naming;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Platform.Onboarding.Rendering;

/// <summary>
/// The platform objects a descriptor yields, by the §7.0 names: what the tenant chart, <c>octopus/terraform</c>,
/// <c>terraform/apps/*</c>, <c>codefresh/register.sh</c> and the conformance tests expect to exist.
/// </summary>
internal static class Inventory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The derived objects of one app, in a stable order.</summary>
    /// <param name="descriptor">The app.</param>
    /// <param name="subscriptionId">Azure subscription ID for the vault hash, or <c>null</c> to print <c>&lt;hash4&gt;</c>.</param>
    public static Dictionary<string, object?> Describe(AppDescriptor descriptor, string? subscriptionId)
    {
        var app = descriptor.Name;
        var environments = new Dictionary<string, object?>();
        foreach (var environment in descriptor.Environments)
        {
            var tier = PlatformNames.TierOf(environment);
            var namespaces = new[] { PlatformNames.Namespace(app, environment) }
                .Concat(descriptor.Parts.Select(part => PlatformNames.Namespace(app, environment, part))).ToArray();
            var applications = descriptor.Deployables.Select(deployable => PlatformNames.Application(app, deployable.Name, environment)).ToList();
            var entry = new Dictionary<string, object?>
            {
                ["tier"] = tier,
                ["cluster"] = $"aks-platform-{tier}",
                ["namespaces"] = namespaces,
                ["hosts"] = namespaces.Select(ns => $"{ns}.<apps-domain-{tier}>").ToArray(),
                ["applications"] = applications,
                ["clusterSecretStore"] = PlatformNames.Namespace(app, environment),
                ["vault"] = PlatformNames.VaultName(app, environment, subscriptionId),
                ["vaultResourceGroup"] = $"rg-platform-{tier}-apps",
                ["vaultKeys"] = VaultKeys(descriptor),
                ["appInsights"] = $"appi-{app}-{environment}",
                ["sloAlert"] = $"slo-fast-burn-{app}-{environment}",
            };
            if (descriptor.Database is { } database)
            {
                applications.Add(PlatformNames.Application(app, PlatformNames.DatabaseDeployable, environment));
                var db = new Dictionary<string, object?>
                {
                    ["engine"] = database.Engine,
                    ["application"] = PlatformNames.Application(app, PlatformNames.DatabaseDeployable, environment),
                    ["folder"] = $"gitops/apps/{app}/envs/{environment}/db/",
                    ["disk"] = $"disk-{app}-{environment}-db",
                    ["diskResourceGroup"] = $"rg-platform-{tier}-data",
                    ["logins"] = new[] { "sa", $"{app}_migrator", $"{app}_app" },
                    ["octopusWorkerAccess"] = database.OctopusWorkerAccess,
                };
                if (environment != "tdd")
                {
                    db["backupContainer"] = $"{app}-{environment}";
                    db["backupCronJob"] = $"db-backup-{app}-{environment}";
                }

                db["restoreCronJob"] = $"db-restore-{app}-{environment}";
                entry["database"] = db;
            }

            if (descriptor.OctopusAzureAccount)
            {
                entry["octopusAccount"] = $"azure-{app}-{environment}";
                entry["deployIdentity"] = $"id-{app}-{environment}-deploy";
            }

            if (descriptor.Azure.WorkloadIdentity)
            {
                entry["appIdentity"] = $"id-{app}-{environment}-app";
                entry["federatedSubject"] = $"system:serviceaccount:{PlatformNames.Namespace(app, environment)}:{descriptor.Azure.ServiceAccount}";
            }

            if (descriptor.Azure.ResourceGroup)
            {
                entry["appResourceGroup"] = $"rg-app-{app}-{tier}";
            }

            environments[environment] = entry;
        }

        var argo = new Dictionary<string, object?>
        {
            ["tenant"] = $"tenant-{app}",
            ["appProject"] = $"app-{app}",
        };
        if (descriptor.Previews)
        {
            argo["previewAppProject"] = $"app-{app}-previews";
        }

        var terraform = new Dictionary<string, object?> { ["appsTierState"] = $"apps-{app}.tfstate" };
        if (descriptor.Azure.Any || descriptor.OctopusAzureAccount)
        {
            terraform["appGrantsState"] = $"app-grants-{app}.tfstate";
        }

        var registry = new Dictionary<string, object?>
        {
            ["repositories"] = descriptor.Deployables.SelectMany(deployable => deployable.Images).Select(image => PlatformNames.Repository(app, image)).ToArray(),
        };
        if (descriptor.Previews)
        {
            registry["previews"] = descriptor.Deployables.SelectMany(deployable => deployable.Images).Select(image => $"apps-previews/{app}/{image}").ToArray();
        }

        return new Dictionary<string, object?>
        {
            ["app"] = app,
            ["status"] = descriptor.Status,
            ["description"] = descriptor.Description,
            ["repositories"] = descriptor.Repositories.Select(repository => $"{repository.Name}@{repository.DefaultBranch}").ToArray(),
            ["argocd"] = argo,
            ["environments"] = environments,
            ["octopus"] = new Dictionary<string, object?>
            {
                ["projectGroup"] = $"app-{app}",
                ["projects"] = descriptor.OctopusProjects.Select(project => new Dictionary<string, object?>
                {
                    ["name"] = project.Name,
                    ["lifecycle"] = project.Lifecycle,
                    ["channels"] = project.Channels,
                    ["configAsCode"] = $".octopus/apps/{app}/{project.Name}/",
                    ["disabled"] = descriptor.IsFrozen,
                }).ToArray(),
            },
            ["codefresh"] = new Dictionary<string, object?>
            {
                ["projects"] = descriptor.CodefreshProjects,
                ["pipelines"] = $"codefresh/apps/{app}/pipelines/",
                ["specs"] = $"codefresh/apps/{app}/specs/",
                ["triggersEnabled"] = !descriptor.IsFrozen,
            },
            ["registry"] = registry,
            ["terraform"] = terraform,
        };
    }

    /// <summary>Renders several apps as YAML documents or a JSON array.</summary>
    /// <param name="descriptors">The apps.</param>
    /// <param name="subscriptionId">Subscription ID for vault names, or <c>null</c>.</param>
    /// <param name="format">yaml or json.</param>
    public static string Render(IEnumerable<AppDescriptor> descriptors, string? subscriptionId, string format)
    {
        var items = descriptors.Select(descriptor => Describe(descriptor, subscriptionId)).ToArray();
        if (format == "json")
        {
            return JsonSerializer.Serialize(items, JsonOptions);
        }

        var serializer = new SerializerBuilder()
            .WithNamingConvention(NullNamingConvention.Instance)
            .DisableAliases()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();
        var builder = new StringBuilder();
        foreach (var item in items)
        {
            builder.Append("---\n").Append(serializer.Serialize(item));
        }

        return builder.ToString();
    }

    /// <summary>A fixed-width inventory table of every app.</summary>
    /// <param name="descriptors">The apps.</param>
    public static string Table(IEnumerable<AppDescriptor> descriptors)
    {
        var rows = new List<string[]> { new[] { "APP", "STATUS", "ENVS", "REPOSITORY", "OCTOPUS", "CODEFRESH", "DEPLOYABLES", "DB", "AZURE", "EXPIRES" } };
        foreach (var descriptor in descriptors)
        {
            rows.Add(
            [
                descriptor.Name,
                descriptor.Status,
                string.Join(",", descriptor.Environments),
                $"{descriptor.PrimaryRepository.Name}@{descriptor.PrimaryRepository.DefaultBranch}",
                string.Join(",", descriptor.OctopusProjects.Select(project => project.Name)),
                string.Join(",", descriptor.CodefreshProjects),
                string.Join(",", descriptor.Deployables.Select(deployable => $"{deployable.Name}({deployable.Packaging})")),
                descriptor.Database?.Engine ?? "-",
                AzureSummary(descriptor),
                descriptor.Expires?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            ]);
        }

        var widths = Enumerable.Range(0, rows[0].Length).Select(column => rows.Max(row => row[column].Length)).ToArray();
        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join("  ", row.Select((cell, column) => column == row.Length - 1 ? cell : cell.PadRight(widths[column]))).TrimEnd());
        }

        return builder.ToString();
    }

    private static string AzureSummary(AppDescriptor descriptor)
    {
        var parts = new List<string>();
        if (descriptor.OctopusAzureAccount)
        {
            parts.Add("account");
        }

        if (descriptor.Azure.ResourceGroup)
        {
            parts.Add("rg");
        }

        if (descriptor.Azure.WorkloadIdentity)
        {
            parts.Add("wi");
        }

        return parts.Count == 0 ? "-" : string.Join(",", parts);
    }

    private static string[] VaultKeys(AppDescriptor descriptor)
    {
        var keys = new List<string>();
        if (descriptor.Database is not null)
        {
            keys.AddRange(["db-sa-password", "db-migrator-password", "db-app-password"]);
        }

        keys.Add("appinsights-connection-string");
        if (descriptor.Azure.WorkloadIdentity)
        {
            keys.Add("azure-client-id");
        }

        keys.AddRange(descriptor.Secrets.Select(secret => secret.Name));
        return keys.ToArray();
    }
}
