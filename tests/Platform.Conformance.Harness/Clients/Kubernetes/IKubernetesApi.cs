using k8s.Models;

namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Kubernetes API of one AKS cluster, authenticated with an Entra token for the AKS server application
/// (clusters use Entra ID and Azure RBAC; local accounts and client certificates are never used).
/// </summary>
public interface IKubernetesApi
{
    /// <summary>Name of the cluster this client talks to.</summary>
    string ClusterName { get; }

    /// <summary>Lists namespaces, optionally by label selector.</summary>
    /// <param name="labelSelector">For example <c>tier=app</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesNamespace>> ListNamespacesAsync(string? labelSelector = null, CancellationToken cancellationToken = default);

    /// <summary>Lists pods in a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="labelSelector">Optional label selector.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesPod>> ListPodsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default);

    /// <summary>Lists Deployments in a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="labelSelector">Optional label selector.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesWorkload>> ListDeploymentsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default);

    /// <summary>Lists StatefulSets in a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="labelSelector">Optional label selector.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesWorkload>> ListStatefulSetsAsync(string namespaceName, string? labelSelector = null, CancellationToken cancellationToken = default);

    /// <summary>Lists persistent volume claims in a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesPersistentVolumeClaim>> ListPersistentVolumeClaimsAsync(string namespaceName, CancellationToken cancellationToken = default);

    /// <summary>Lists resource quotas in a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesResourceQuota>> ListResourceQuotasAsync(string namespaceName, CancellationToken cancellationToken = default);

    /// <summary>Lists network policies in a namespace.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesNetworkPolicy>> ListNetworkPoliciesAsync(string namespaceName, CancellationToken cancellationToken = default);

    /// <summary>Creates a namespaced object of any API group (for example a batch/v1 Job) and returns it as stored.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="body">The object as JSON.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<System.Text.Json.JsonElement> CreateNamespacedObjectAsync(CustomResourceKind kind, string namespaceName, System.Text.Json.JsonElement body, CancellationToken cancellationToken = default);

    /// <summary>Gets a custom object as JSON; <c>null</c> when it does not exist.</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace, or <c>null</c> for a cluster-scoped kind.</param>
    /// <param name="name">Object name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<System.Text.Json.JsonElement?> GetCustomObjectAsync(CustomResourceKind kind, string? namespaceName, string name, CancellationToken cancellationToken = default);

    /// <summary>Lists custom objects as JSON (the <c>items</c> of the list).</summary>
    /// <param name="kind">Group, version and plural.</param>
    /// <param name="namespaceName">Namespace, or <c>null</c> for a cluster-scoped kind or all namespaces.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<System.Text.Json.JsonElement>> ListCustomObjectsAsync(CustomResourceKind kind, string? namespaceName, CancellationToken cancellationToken = default);

    /// <summary>Gets the sync and health status of an Argo CD Application.</summary>
    /// <param name="name">Application name, for example <c>workorders-tdd</c>.</param>
    /// <param name="namespaceName">Argo CD namespace (default <c>argocd</c>).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="PlatformApiException">The Application does not exist.</exception>
    Task<ArgoApplicationStatus> GetArgoApplicationAsync(string name, string namespaceName = "argocd", CancellationToken cancellationToken = default);

    /// <summary>Lists Kyverno policies of every installed kind (ValidatingPolicy, ImageValidatingPolicy, ClusterPolicy).</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KyvernoPolicyStatus>> ListKyvernoPoliciesAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists ExternalSecrets in a namespace with their Ready condition.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<ExternalSecretStatus>> ListExternalSecretsAsync(string namespaceName, CancellationToken cancellationToken = default);

    /// <summary>Lists the Events of one object in a namespace (field selector <c>involvedObject.name</c>).</summary>
    /// <param name="namespaceName">Namespace of the object.</param>
    /// <param name="objectName">Name of the object.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<KubernetesEvent>> ListEventsAsync(string namespaceName, string objectName, CancellationToken cancellationToken = default);

    /// <summary>Creates a pod (optionally as a server-side dry run); an admission rejection is returned, not thrown.</summary>
    /// <param name="pod">The pod, with metadata name and namespace.</param>
    /// <param name="dryRun"><c>true</c> for a server-side dry run: admission runs, nothing is stored.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<PodCreationResult> CreatePodAsync(V1Pod pod, bool dryRun = false, CancellationToken cancellationToken = default);

    /// <summary>Deletes a pod; succeeds when it is already gone.</summary>
    /// <param name="namespaceName">Namespace.</param>
    /// <param name="name">Pod name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task DeletePodAsync(string namespaceName, string name, CancellationToken cancellationToken = default);
}
