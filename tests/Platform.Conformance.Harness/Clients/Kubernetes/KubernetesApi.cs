using System.Net;
using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace Platform.Conformance.Harness.Clients;

/// <summary>KubernetesClient implementation of <see cref="IKubernetesApi"/>.</summary>
public sealed class KubernetesApi : IKubernetesApi, IDisposable
{
    private readonly IKubernetes client;

    /// <summary>Creates the client over a configured KubernetesClient instance.</summary>
    /// <param name="client">The KubernetesClient; owned and disposed by this instance.</param>
    /// <param name="clusterName">Cluster name, for messages.</param>
    public KubernetesApi(IKubernetes client, string clusterName)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(clusterName);
        this.client = client;
        ClusterName = clusterName;
    }

    /// <inheritdoc />
    public string ClusterName { get; }

    /// <summary>
    /// Connects to an AKS cluster: checks that it runs (a stopped cluster makes the test Inconclusive), reads its user
    /// kubeconfig through ARM (server and CA only) and authenticates every request with an Entra token for the AKS server
    /// application. Reads that fail transiently are retried (<see cref="TransientRetryHandler"/>).
    /// </summary>
    /// <param name="azure">Azure client used to read the power state and the kubeconfig.</param>
    /// <param name="tokenProvider">Entra token provider (see <see cref="EntraTokenProvider"/>).</param>
    /// <param name="resourceGroup">Resource group of the cluster.</param>
    /// <param name="clusterName">Cluster name.</param>
    /// <param name="systemTrust"><c>true</c> to validate TLS against the system trust store instead of pinning the cluster CA.</param>
    /// <param name="cancellationToken">Cancels the ARM calls.</param>
    /// <exception cref="PlatformPrerequisiteException">The cluster is stopped (the test becomes Inconclusive).</exception>
    public static async Task<KubernetesApi> ConnectAsync(IAzureApi azure, k8s.Authentication.ITokenProvider tokenProvider, string resourceGroup, string clusterName, bool systemTrust, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(azure);
        await KubernetesConnection.RequireRunningAsync(azure, resourceGroup, clusterName, cancellationToken).ConfigureAwait(false);
        var kubeconfig = await azure.GetClusterUserKubeconfigAsync(resourceGroup, clusterName, cancellationToken).ConfigureAwait(false);
        var endpoint = KubernetesConnection.ReadEndpoint(kubeconfig);
        var configuration = KubernetesConnection.CreateConfiguration(endpoint, tokenProvider, systemTrust);
        return new KubernetesApi(new Kubernetes(configuration, new TransientRetryHandler()), clusterName);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesNamespace>> ListNamespacesAsync(string? labelSelector = null, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesNamespace>>("GET", "namespaces", async () =>
        {
            var list = await client.CoreV1.ListNamespaceAsync(labelSelector: labelSelector, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(item => new KubernetesNamespace(item.Metadata.Name, item.Status?.Phase, Labels(item.Metadata.Labels))).ToArray();
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesPod>> ListPodsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesPod>>("GET", $"namespaces/{namespaceName}/pods", async () =>
        {
            var list = await client.CoreV1.ListNamespacedPodAsync(namespaceName, labelSelector: labelSelector, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(pod => new KubernetesPod(
                    namespaceName,
                    pod.Metadata.Name,
                    pod.Status?.Phase,
                    pod.Status?.ContainerStatuses is { Count: > 0 } statuses && statuses.All(status => status.Ready),
                    pod.Spec?.NodeName,
                    pod.Spec?.Containers?.Select(container => container.Image).ToArray() ?? [],
                    Labels(pod.Metadata.Labels)))
                .ToArray();
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesWorkload>> ListDeploymentsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesWorkload>>("GET", $"namespaces/{namespaceName}/deployments", async () =>
        {
            var list = await client.AppsV1.ListNamespacedDeploymentAsync(namespaceName, labelSelector: labelSelector, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(deployment => new KubernetesWorkload(
                    "Deployment",
                    namespaceName,
                    deployment.Metadata.Name,
                    deployment.Spec?.Replicas ?? 0,
                    deployment.Status?.ReadyReplicas ?? 0,
                    deployment.Status?.AvailableReplicas ?? 0,
                    deployment.Spec?.Template?.Spec?.Containers?.Select(container => container.Image).ToArray() ?? []))
                .ToArray();
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesWorkload>> ListStatefulSetsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesWorkload>>("GET", $"namespaces/{namespaceName}/statefulsets", async () =>
        {
            var list = await client.AppsV1.ListNamespacedStatefulSetAsync(namespaceName, labelSelector: labelSelector, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(set => new KubernetesWorkload(
                    "StatefulSet",
                    namespaceName,
                    set.Metadata.Name,
                    set.Spec?.Replicas ?? 0,
                    set.Status?.ReadyReplicas ?? 0,
                    set.Status?.AvailableReplicas ?? 0,
                    set.Spec?.Template?.Spec?.Containers?.Select(container => container.Image).ToArray() ?? []))
                .ToArray();
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesPersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(string namespaceName, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesPersistentVolumeClaim>>("GET", $"namespaces/{namespaceName}/persistentvolumeclaims", async () =>
        {
            var list = await client.CoreV1.ListNamespacedPersistentVolumeClaimAsync(namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(claim => new KubernetesPersistentVolumeClaim(
                    namespaceName,
                    claim.Metadata.Name,
                    claim.Status?.Phase,
                    claim.Spec?.StorageClassName,
                    claim.Status?.Capacity is { } capacity && capacity.TryGetValue("storage", out var storage) ? storage.ToString() : null,
                    claim.Spec?.VolumeName))
                .ToArray();
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesResourceQuota>> ListResourceQuotasAsync(string namespaceName, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesResourceQuota>>("GET", $"namespaces/{namespaceName}/resourcequotas", async () =>
        {
            var list = await client.CoreV1.ListNamespacedResourceQuotaAsync(namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(quota => new KubernetesResourceQuota(
                    namespaceName,
                    quota.Metadata.Name,
                    Quantities(quota.Status?.Hard ?? quota.Spec?.Hard),
                    Quantities(quota.Status?.Used)))
                .ToArray();
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<KubernetesNetworkPolicy>> ListNetworkPoliciesAsync(string namespaceName, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<KubernetesNetworkPolicy>>("GET", $"namespaces/{namespaceName}/networkpolicies", async () =>
        {
            var list = await client.NetworkingV1.ListNamespacedNetworkPolicyAsync(namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.Items.Select(policy => new KubernetesNetworkPolicy(
                    namespaceName,
                    policy.Metadata.Name,
                    Labels(policy.Spec?.PodSelector?.MatchLabels),
                    policy.Spec?.PolicyTypes?.ToArray() ?? []))
                .ToArray();
        });

    /// <inheritdoc />
    public async Task<JsonElement?> GetCustomObjectAsync(CustomResourceKind kind, string? namespaceName, string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            return namespaceName is null
                ? await client.CustomObjects.GetClusterCustomObjectAsync<JsonElement>(kind.Group, kind.Version, kind.Plural, name, cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.GetNamespacedCustomObjectAsync<JsonElement>(kind.Group, kind.Version, namespaceName, kind.Plural, name, cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public Task<IReadOnlyList<JsonElement>> ListCustomObjectsAsync(CustomResourceKind kind, string? namespaceName, CancellationToken cancellationToken = default) =>
        CallAsync<IReadOnlyList<JsonElement>>("GET", $"{kind}/{namespaceName}", async () =>
        {
            ArgumentNullException.ThrowIfNull(kind);
            var list = namespaceName is null
                ? await client.CustomObjects.ListClusterCustomObjectAsync<JsonElement>(kind.Group, kind.Version, kind.Plural, cancellationToken: cancellationToken).ConfigureAwait(false)
                : await client.CustomObjects.ListNamespacedCustomObjectAsync<JsonElement>(kind.Group, kind.Version, namespaceName, kind.Plural, cancellationToken: cancellationToken).ConfigureAwait(false);
            return list.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToArray() : [];
        });

    /// <inheritdoc />
    public async Task<ArgoApplicationStatus> GetArgoApplicationAsync(string name, string namespaceName = "argocd", CancellationToken cancellationToken = default)
    {
        var application = await GetCustomObjectAsync(CustomResourceKind.ArgoApplication, namespaceName, name, cancellationToken).ConfigureAwait(false)
            ?? throw new PlatformApiException("Kubernetes", "GET", $"{CustomResourceKind.ArgoApplication}/{namespaceName}/{name}", HttpStatusCode.NotFound, $"Argo CD Application {name} does not exist on {ClusterName}.");
        var status = Child(application, "status");
        return new ArgoApplicationStatus(
            name,
            Text(Child(status, "sync"), "status"),
            Text(Child(status, "health"), "status"),
            Text(Child(status, "sync"), "revision"),
            Text(Child(Child(application, "spec"), "source"), "targetRevision"),
            Text(Child(status, "operationState"), "phase"));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KyvernoPolicyStatus>> ListKyvernoPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policies = new List<KyvernoPolicyStatus>();
        var kinds = new (CustomResourceKind Kind, string Name)[]
        {
            (CustomResourceKind.KyvernoValidatingPolicy, "ValidatingPolicy"),
            (CustomResourceKind.KyvernoImageValidatingPolicy, "ImageValidatingPolicy"),
            (CustomResourceKind.KyvernoClusterPolicy, "ClusterPolicy"),
        };
        foreach (var (kind, kindName) in kinds)
        {
            IReadOnlyList<JsonElement> items;
            try
            {
                items = await ListCustomObjectsAsync(kind, namespaceName: null, cancellationToken).ConfigureAwait(false);
            }
            catch (PlatformApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            policies.AddRange(items.Select(policy => new KyvernoPolicyStatus(kindName, Text(Child(policy, "metadata"), "name") ?? "", ValidationActions(policy), PolicyReady(policy))));
        }

        return policies;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExternalSecretStatus>> ListExternalSecretsAsync(string namespaceName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        var items = await ListCustomObjectsAsync(CustomResourceKind.ExternalSecret, namespaceName, cancellationToken).ConfigureAwait(false);
        return items.Select(secret =>
            {
                var status = Child(secret, "status");
                var ready = Conditions(status).FirstOrDefault(condition => Text(condition, "type") == "Ready");
                return new ExternalSecretStatus(
                    namespaceName,
                    Text(Child(secret, "metadata"), "name") ?? "",
                    Text(ready, "status") == "True",
                    Text(ready, "reason"),
                    Text(ready, "message"),
                    Text(status, "refreshTime") is { } refreshed && DateTimeOffset.TryParse(refreshed, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : null);
            })
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KubernetesEvent>> ListEventsAsync(string namespaceName, string objectName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        var events = await client.CoreV1.ListNamespacedEventAsync(namespaceName, fieldSelector: $"involvedObject.name={objectName}", cancellationToken: cancellationToken).ConfigureAwait(false);
        return events.Items
            .Select(item => new KubernetesEvent(
                item.Type,
                item.Reason,
                item.Message,
                item.LastTimestamp is { } last ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)) : item.EventTime is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<PodCreationResult> CreatePodAsync(V1Pod pod, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pod);
        var name = pod.Metadata?.Name ?? throw new ArgumentException("The pod needs metadata.name.", nameof(pod));
        var namespaceName = pod.Metadata.NamespaceProperty ?? throw new ArgumentException("The pod needs metadata.namespace.", nameof(pod));
        try
        {
            var created = await client.CoreV1.CreateNamespacedPodAsync(pod, namespaceName, dryRun: dryRun ? "All" : null, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new PodCreationResult(true, created.Metadata?.Name ?? name, null, null);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity)
        {
            return new PodCreationResult(false, name, (int)ex.Response.StatusCode, StatusMessage(ex.Response.Content) ?? ex.Message);
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "POST", $"namespaces/{namespaceName}/pods");
        }
    }

    /// <inheritdoc />
    public async Task DeletePodAsync(string namespaceName, string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            await client.CoreV1.DeleteNamespacedPodAsync(name, namespaceName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == HttpStatusCode.NotFound)
        {
        }
        catch (HttpOperationException ex)
        {
            throw Translate(ex, "DELETE", $"namespaces/{namespaceName}/pods/{name}");
        }
    }

    /// <summary>Disposes the KubernetesClient.</summary>
    public void Dispose() => client.Dispose();

    private async Task<T> CallAsync<T>(string method, string path, Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
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

    private static IReadOnlyDictionary<string, string> Labels(IDictionary<string, string>? labels) =>
        labels is null ? new Dictionary<string, string>() : new Dictionary<string, string>(labels);

    private static IReadOnlyDictionary<string, string> Quantities(IDictionary<string, ResourceQuantity>? quantities) =>
        quantities?.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()) ?? new Dictionary<string, string>();

    private static IReadOnlyList<string> ValidationActions(JsonElement policy)
    {
        var spec = Child(policy, "spec");
        if (spec.ValueKind == JsonValueKind.Object && spec.TryGetProperty("validationActions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            return actions.EnumerateArray().Where(action => action.ValueKind == JsonValueKind.String).Select(action => action.GetString()!).ToArray();
        }

        return Text(spec, "validationFailureAction") is { } failureAction ? [failureAction] : [];
    }

    private static bool? PolicyReady(JsonElement policy)
    {
        var status = Child(policy, "status");
        if (Child(status, "conditionStatus") is { ValueKind: JsonValueKind.Object } conditionStatus && conditionStatus.TryGetProperty("ready", out var readyFlag) && readyFlag.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return readyFlag.GetBoolean();
        }

        if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("ready", out var ready) && ready.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return ready.GetBoolean();
        }

        var condition = Conditions(status).FirstOrDefault(candidate => Text(candidate, "type") == "Ready");
        return condition.ValueKind == JsonValueKind.Object ? Text(condition, "status") == "True" : null;
    }

    private static IEnumerable<JsonElement> Conditions(JsonElement status) =>
        status.ValueKind == JsonValueKind.Object && status.TryGetProperty("conditions", out var conditions) && conditions.ValueKind == JsonValueKind.Array
            ? conditions.EnumerateArray()
            : [];

    private static JsonElement Child(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var child) ? child : default;

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
