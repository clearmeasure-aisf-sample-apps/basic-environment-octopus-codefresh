using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Platform.Conformance.Harness.Clients;
using Platform.Conformance.Harness.Settings;

namespace Platform.Conformance.Tests.GitOps;

/// <summary>Outcome of a create that admission control may refuse.</summary>
/// <param name="Admitted"><c>true</c> when the API server accepted the object.</param>
/// <param name="StatusCode">HTTP status of a refusal.</param>
/// <param name="Message">Refusal message, for example from a Kyverno policy or the ResourceQuota admission plugin.</param>
public sealed record AdmissionResult(bool Admitted, int? StatusCode, string? Message);

/// <summary>A probe pod that ran to completion.</summary>
/// <param name="Phase"><c>Succeeded</c> or <c>Failed</c>.</param>
/// <param name="ExitCode">Exit code of its container.</param>
/// <param name="Log">Its log.</param>
public sealed record ProbeResult(string Phase, int? ExitCode, string Log);

/// <summary>
/// The Kubernetes calls the GitOps tests need beyond <see cref="IKubernetesApi"/>: custom objects (create with server-side
/// dry run, patch, delete), probe pods with logs, and reads of Jobs, ReplicaSets, Secrets, PersistentVolumes, CronJobs,
/// LimitRanges and ConfigMaps. It connects exactly like the harness: the cluster's user kubeconfig through ARM for the
/// API server and CA only, and an Entra token for the AKS server application on every request.
/// </summary>
public sealed class GitOpsCluster : IDisposable
{
    /// <summary>Label that marks every object a conformance run creates.</summary>
    public const string RunLabel = "conformance-run";

    /// <summary>Time-to-live label of Kubernetes fixtures; leftovers older than this are removed by the next run.</summary>
    public const string TtlLabel = "cleanup.kyverno.io/ttl";

    private static readonly TimeSpan LeftoverAge = TimeSpan.FromHours(1);

    private GitOpsCluster(IKubernetes client, string clusterName)
    {
        Client = client;
        ClusterName = clusterName;
    }

    /// <summary>The KubernetesClient instance.</summary>
    public IKubernetes Client { get; }

    /// <summary>Cluster name, for messages.</summary>
    public string ClusterName { get; }

    /// <summary>
    /// Connects to the cluster of a tier; Inconclusive when a setting or credential is missing or the cluster is stopped.
    /// Reads that fail transiently are retried (<see cref="TransientRetryHandler"/>).
    /// </summary>
    /// <param name="settings">Harness settings.</param>
    /// <param name="azure">The harness Azure client (reads the power state and the kubeconfig).</param>
    /// <param name="tier">The tier.</param>
    /// <param name="cancellationToken">Cancels the connection.</param>
    public static async Task<GitOpsCluster> ConnectAsync(PlatformSettings settings, IAzureApi azure, PlatformTier tier, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(azure);
        var tierSettings = settings.Tier(tier);
        var key = GitOpsNames.Key(tier);
        settings.Check($"the Kubernetes API of the {key} cluster (GitOps tests)")
            .Setting($"Tiers.{key}.ResourceGroup", tierSettings.ResourceGroup)
            .Setting($"Tiers.{key}.ClusterName", tierSettings.ClusterName)
            .ThrowIfMissing();
        await KubernetesConnection.RequireRunningAsync(azure, tierSettings.ResourceGroup!, tierSettings.ClusterName!, cancellationToken).ConfigureAwait(false);
        var kubeconfig = await azure.GetClusterUserKubeconfigAsync(tierSettings.ResourceGroup!, tierSettings.ClusterName!, cancellationToken).ConfigureAwait(false);
        var endpoint = KubernetesConnection.ReadEndpoint(kubeconfig);
        var configuration = KubernetesConnection.CreateConfiguration(endpoint, new EntraTokenProvider(AzureCredentialFactory.Create(settings)), settings.TlsSystemTrust);
        return new GitOpsCluster(new Kubernetes(configuration, new TransientRetryHandler()), tierSettings.ClusterName!);
    }

    /// <summary>Labels of a fixture this run creates: the run label and the one-hour time to live.</summary>
    /// <param name="runId">Run ID.</param>
    public static Dictionary<string, string> FixtureLabels(string runId) => new(StringComparer.Ordinal)
    {
        [RunLabel] = LabelValue(runId),
        [TtlLabel] = "1h",
    };

    /// <summary>Creates a custom object, optionally as a server-side dry run; a refusal is returned, not thrown.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="body">The object.</param>
    /// <param name="dryRun"><c>true</c>: admission runs, nothing is stored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<AdmissionResult> CreateAsync(CustomResourceKind kind, string namespaceName, JsonObject body, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        try
        {
            await Client.CustomObjects.CreateNamespacedCustomObjectAsync(body, kind.Group, kind.Version, namespaceName, kind.Plural, dryRun: dryRun ? "All" : null, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AdmissionResult(true, null, null);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
        {
            return new AdmissionResult(false, (int)ex.Response.StatusCode, StatusMessage(ex.Response.Content) ?? ex.Message);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "POST", $"{kind}/{namespaceName}");
        }
    }

    /// <summary>Gets a custom object; <c>null</c> when it does not exist.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace, or <c>null</c> for a cluster-scoped kind.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<JsonElement?> GetAsync(CustomResourceKind kind, string? namespaceName, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        try
        {
            return namespaceName is null
                ? await Client.CustomObjects.GetClusterCustomObjectAsync<JsonElement>(kind.Group, kind.Version, kind.Plural, name, cancellationToken).ConfigureAwait(false)
                : await Client.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(kind.Group, kind.Version, namespaceName, kind.Plural, name, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "GET", $"{kind}/{namespaceName}/{name}");
        }
    }

    /// <summary>Applies a JSON merge patch to a custom object.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="patch">The merge patch.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task MergePatchAsync(CustomResourceKind kind, string namespaceName, string name, JsonObject patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(patch);
        try
        {
            await Client.CustomObjects.PatchNamespacedCustomObjectAsync(new V1Patch(patch.ToJsonString(), V1Patch.PatchType.MergePatch), kind.Group, kind.Version, namespaceName, kind.Plural, name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "PATCH", $"{kind}/{namespaceName}/{name}");
        }
    }

    /// <summary>Deletes a custom object; succeeds when it is already gone.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task DeleteAsync(CustomResourceKind kind, string namespaceName, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        try
        {
            await Client.CustomObjects.DeleteNamespacedCustomObjectAsync(kind.Group, kind.Version, namespaceName, kind.Plural, name, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "DELETE", $"{kind}/{namespaceName}/{name}");
        }
    }

    /// <summary>Deletes custom objects of earlier runs (label <see cref="RunLabel"/>) older than one hour.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    public async Task RemoveLeftoversAsync(CustomResourceKind kind, string namespaceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(kind);
        JsonElement list;
        try
        {
            list = await Client.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>(kind.Group, kind.Version, namespaceName, kind.Plural, labelSelector: RunLabel, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "GET", $"{kind}/{namespaceName}?labelSelector={RunLabel}");
        }

        var items = list.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array ? array.EnumerateArray().ToArray() : [];
        foreach (var item in items.Where(item => IsOlderThan(Text(item, "metadata", "creationTimestamp"), LeftoverAge)))
        {
            if (Text(item, "metadata", "name") is { } name)
            {
                await DeleteAsync(kind, namespaceName, name, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs a probe pod to completion and returns its phase, exit code and log. The pod is deleted afterwards.</summary>
    /// <param name="pod">The pod, with metadata name and namespace and <c>restartPolicy: Never</c>.</param>
    /// <param name="timeout">Longest wait for completion.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    public async Task<ProbeResult> RunProbePodAsync(V1Pod pod, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var name = pod.Metadata.Name;
        var namespaceName = pod.Metadata.NamespaceProperty;
        await RemoveLeftoverPodsAsync(namespaceName, cancellationToken).ConfigureAwait(false);
        try
        {
            await Client.CoreV1.CreateNamespacedPodAsync(pod, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "POST", $"namespaces/{namespaceName}/pods");
        }

        try
        {
            var finished = await Harness.Support.Poll.UntilAsync(
                    async token => await Client.CoreV1.ReadNamespacedPodAsync(name, namespaceName, cancellationToken: token).ConfigureAwait(false),
                    current => current.Status?.Phase is "Succeeded" or "Failed",
                    timeout,
                    TimeSpan.FromSeconds(3),
                    $"probe pod {namespaceName}/{name} on {ClusterName} to finish",
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var exitCode = finished.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Terminated?.ExitCode;
            return new ProbeResult(finished.Status!.Phase, exitCode, await PodLogAsync(namespaceName, name, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            await DeletePodAsync(namespaceName, name, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the log of a pod's first container.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Pod name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<string> PodLogAsync(string namespaceName, string name, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await Client.CoreV1.ReadNamespacedPodLogAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "GET", $"namespaces/{namespaceName}/pods/{name}/log");
        }
    }

    /// <summary>Deletes a pod; succeeds when it is already gone.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Pod name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task DeletePodAsync(string namespaceName, string name, CancellationToken cancellationToken)
    {
        try
        {
            await Client.CoreV1.DeleteNamespacedPodAsync(name, namespaceName, gracePeriodSeconds: 0, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "DELETE", $"namespaces/{namespaceName}/pods/{name}");
        }
    }

    /// <summary>Reads a Deployment; <c>null</c> when it does not exist.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1Deployment?> DeploymentAsync(string namespaceName, string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"namespaces/{namespaceName}/deployments/{name}", () => Client.AppsV1.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken));

    /// <summary>Sets one metadata label of a Deployment (JSON merge patch).</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Deployment name.</param>
    /// <param name="label">Label key.</param>
    /// <param name="value">Label value.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task SetDeploymentLabelAsync(string namespaceName, string name, string label, string value, CancellationToken cancellationToken)
    {
        var patch = new JsonObject { ["metadata"] = new JsonObject { ["labels"] = new JsonObject { [label] = value } } };
        try
        {
            await Client.AppsV1.PatchNamespacedDeploymentAsync(new V1Patch(patch.ToJsonString(), V1Patch.PatchType.MergePatch), name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "PATCH", $"namespaces/{namespaceName}/deployments/{name}");
        }
    }

    /// <summary>ReplicaSets of a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<IReadOnlyList<V1ReplicaSet>> ReplicaSetsAsync(string namespaceName, CancellationToken cancellationToken)
    {
        try
        {
            var list = await Client.AppsV1.ListNamespacedReplicaSetAsync(namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.ToArray();
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "GET", $"namespaces/{namespaceName}/replicasets");
        }
    }

    /// <summary>Reads a Job; <c>null</c> when it does not exist.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1Job?> JobAsync(string namespaceName, string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"namespaces/{namespaceName}/jobs/{name}", () => Client.BatchV1.ReadNamespacedJobAsync(name, namespaceName, cancellationToken: cancellationToken));

    /// <summary>Reads a CronJob; <c>null</c> when it does not exist.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1CronJob?> CronJobAsync(string namespaceName, string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"namespaces/{namespaceName}/cronjobs/{name}", () => Client.BatchV1.ReadNamespacedCronJobAsync(name, namespaceName, cancellationToken: cancellationToken));

    /// <summary>Reads a Secret; <c>null</c> when it does not exist.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1Secret?> SecretAsync(string namespaceName, string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"namespaces/{namespaceName}/secrets/{name}", () => Client.CoreV1.ReadNamespacedSecretAsync(name, namespaceName, cancellationToken: cancellationToken));

    /// <summary>Reads a PersistentVolume; <c>null</c> when it does not exist.</summary>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1PersistentVolume?> PersistentVolumeAsync(string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"persistentvolumes/{name}", () => Client.CoreV1.ReadPersistentVolumeAsync(name, cancellationToken: cancellationToken));

    /// <summary>Reads a LimitRange; <c>null</c> when it does not exist.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1LimitRange?> LimitRangeAsync(string namespaceName, string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"namespaces/{namespaceName}/limitranges/{name}", () => Client.CoreV1.ReadNamespacedLimitRangeAsync(name, namespaceName, cancellationToken: cancellationToken));

    /// <summary>Reads a ConfigMap; <c>null</c> when it does not exist.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<V1ConfigMap?> ConfigMapAsync(string namespaceName, string name, CancellationToken cancellationToken) =>
        ReadOrNullAsync("GET", $"namespaces/{namespaceName}/configmaps/{name}", () => Client.CoreV1.ReadNamespacedConfigMapAsync(name, namespaceName, cancellationToken: cancellationToken));

    /// <summary>Disposes the KubernetesClient.</summary>
    public void Dispose() => Client.Dispose();

    /// <summary>A string property at a path of a JSON object, or <c>null</c>.</summary>
    /// <param name="element">The object.</param>
    /// <param name="path">Property names.</param>
    public static string? Text(JsonElement element, params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    /// <summary>A child element at a path, or <c>default</c> (<see cref="JsonValueKind.Undefined"/>).</summary>
    /// <param name="element">The object.</param>
    /// <param name="path">Property names.</param>
    public static JsonElement Child(JsonElement element, params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return default;
            }
        }

        return current;
    }

    /// <summary>The <c>Ready</c> condition of a status (<c>status.conditions[type=Ready]</c>): its status and message.</summary>
    /// <param name="element">The object.</param>
    public static (bool? Ready, string? Message) ReadyCondition(JsonElement element)
    {
        var conditions = Child(element, "status", "conditions");
        if (conditions.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var condition in conditions.EnumerateArray().Where(condition => Text(condition, "type") == "Ready"))
        {
            return (Text(condition, "status") == "True", Text(condition, "message"));
        }

        return (null, null);
    }

    /// <summary>Parses a Kubernetes timestamp.</summary>
    /// <param name="value">RFC 3339 text.</param>
    public static DateTimeOffset? Timestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static bool IsOlderThan(string? timestamp, TimeSpan age) => Timestamp(timestamp) is { } time && DateTimeOffset.UtcNow - time > age;

    private static string LabelValue(string value)
    {
        var cleaned = new string((value ?? string.Empty).Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-').ToArray()).Trim('-', '_', '.');
        return cleaned.Length > 63 ? cleaned[..63].TrimEnd('-', '_', '.') : cleaned;
    }

    private async Task RemoveLeftoverPodsAsync(string namespaceName, CancellationToken cancellationToken)
    {
        V1PodList pods;
        try
        {
            pods = await Client.CoreV1.ListNamespacedPodAsync(namespaceName, labelSelector: RunLabel, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "GET", $"namespaces/{namespaceName}/pods?labelSelector={RunLabel}");
        }

        foreach (var pod in pods.Items.Where(pod => pod.Metadata.CreationTimestamp is { } created && DateTime.UtcNow - created.ToUniversalTime() > LeftoverAge))
        {
            await DeletePodAsync(namespaceName, pod.Metadata.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<T?> ReadOrNullAsync<T>(string method, string path, Func<Task<T>> call)
        where T : class
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, method, path);
        }
    }

    private PlatformApiException Translate(HttpOperationException exception, string method, string path) =>
        new($"Kubernetes ({ClusterName})", method, path, exception.Response?.StatusCode, StatusMessage(exception.Response?.Content) ?? exception.Message, exception);

    private static string? StatusMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            return Text(document.RootElement, "message") ?? content;
        }
        catch (JsonException)
        {
            return content;
        }
    }
}
