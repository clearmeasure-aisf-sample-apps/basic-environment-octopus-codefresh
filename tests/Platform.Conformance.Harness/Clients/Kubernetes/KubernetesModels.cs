namespace Platform.Conformance.Harness.Clients;

/// <summary>A namespace and its labels.</summary>
/// <param name="Name">Namespace name.</param>
/// <param name="Phase"><c>Active</c> or <c>Terminating</c>.</param>
/// <param name="Labels">Labels.</param>
public sealed record KubernetesNamespace(string Name, string? Phase, IReadOnlyDictionary<string, string> Labels);

/// <summary>A pod.</summary>
/// <param name="Namespace">Namespace.</param>
/// <param name="Name">Pod name.</param>
/// <param name="Phase">Phase, for example <c>Running</c>.</param>
/// <param name="Ready"><c>true</c> when every container is ready.</param>
/// <param name="NodeName">Node it runs on.</param>
/// <param name="Images">Container images.</param>
/// <param name="Labels">Labels.</param>
public sealed record KubernetesPod(string Namespace, string Name, string? Phase, bool Ready, string? NodeName, IReadOnlyList<string> Images, IReadOnlyDictionary<string, string> Labels);

/// <summary>A Deployment or StatefulSet and its replica counts.</summary>
/// <param name="Kind"><c>Deployment</c> or <c>StatefulSet</c>.</param>
/// <param name="Namespace">Namespace.</param>
/// <param name="Name">Name.</param>
/// <param name="DesiredReplicas">Replicas in the spec.</param>
/// <param name="ReadyReplicas">Ready replicas.</param>
/// <param name="AvailableReplicas">Available replicas.</param>
/// <param name="Images">Container images of the pod template.</param>
public sealed record KubernetesWorkload(string Kind, string Namespace, string Name, int DesiredReplicas, int ReadyReplicas, int AvailableReplicas, IReadOnlyList<string> Images);

/// <summary>A persistent volume claim.</summary>
/// <param name="Namespace">Namespace.</param>
/// <param name="Name">Name.</param>
/// <param name="Phase">Phase, for example <c>Bound</c>.</param>
/// <param name="StorageClass">Storage class.</param>
/// <param name="Capacity">Bound capacity, for example <c>8Gi</c>.</param>
/// <param name="VolumeName">Bound volume.</param>
public sealed record KubernetesPersistentVolumeClaim(string Namespace, string Name, string? Phase, string? StorageClass, string? Capacity, string? VolumeName);

/// <summary>A resource quota.</summary>
/// <param name="Namespace">Namespace.</param>
/// <param name="Name">Name.</param>
/// <param name="Hard">Limits by resource.</param>
/// <param name="Used">Usage by resource.</param>
public sealed record KubernetesResourceQuota(string Namespace, string Name, IReadOnlyDictionary<string, string> Hard, IReadOnlyDictionary<string, string> Used);

/// <summary>A network policy.</summary>
/// <param name="Namespace">Namespace.</param>
/// <param name="Name">Name.</param>
/// <param name="PodSelector">Match labels of the pod selector (empty selects every pod).</param>
/// <param name="PolicyTypes"><c>Ingress</c> and/or <c>Egress</c>.</param>
public sealed record KubernetesNetworkPolicy(string Namespace, string Name, IReadOnlyDictionary<string, string> PodSelector, IReadOnlyList<string> PolicyTypes);

/// <summary>Group, version and plural of a custom resource.</summary>
/// <param name="Group">API group.</param>
/// <param name="Version">API version.</param>
/// <param name="Plural">Plural resource name.</param>
public sealed record CustomResourceKind(string Group, string Version, string Plural)
{
    /// <summary>Argo CD Application (<c>argoproj.io/v1alpha1</c>).</summary>
    public static CustomResourceKind ArgoApplication { get; } = new("argoproj.io", "v1alpha1", "applications");

    /// <summary>Kyverno ClusterPolicy (<c>kyverno.io/v1</c>).</summary>
    public static CustomResourceKind KyvernoClusterPolicy { get; } = new("kyverno.io", "v1", "clusterpolicies");

    /// <summary>Kyverno ValidatingPolicy (<c>policies.kyverno.io/v1</c>), the kind the platform's policies use.</summary>
    public static CustomResourceKind KyvernoValidatingPolicy { get; } = new("policies.kyverno.io", "v1", "validatingpolicies");

    /// <summary>Kyverno ImageValidatingPolicy (<c>policies.kyverno.io/v1</c>).</summary>
    public static CustomResourceKind KyvernoImageValidatingPolicy { get; } = new("policies.kyverno.io", "v1", "imagevalidatingpolicies");

    /// <summary>External Secrets ExternalSecret (<c>external-secrets.io/v1</c>).</summary>
    public static CustomResourceKind ExternalSecret { get; } = new("external-secrets.io", "v1", "externalsecrets");

    /// <summary><c>plural.group/version</c>.</summary>
    public override string ToString() => $"{Plural}.{Group}/{Version}";
}

/// <summary>Sync and health of an Argo CD Application.</summary>
/// <param name="Name">Application name.</param>
/// <param name="SyncStatus"><c>Synced</c> or <c>OutOfSync</c>.</param>
/// <param name="HealthStatus">For example <c>Healthy</c>, <c>Progressing</c> or <c>Degraded</c>.</param>
/// <param name="Revision">Git revision it is synced to.</param>
/// <param name="TargetRevision">Revision it tracks, for example <c>main</c>.</param>
/// <param name="OperationPhase">Phase of the last operation, for example <c>Succeeded</c>.</param>
public sealed record ArgoApplicationStatus(string Name, string? SyncStatus, string? HealthStatus, string? Revision, string? TargetRevision, string? OperationPhase)
{
    /// <summary><c>true</c> when Synced and Healthy.</summary>
    public bool IsSyncedAndHealthy =>
        string.Equals(SyncStatus, "Synced", StringComparison.OrdinalIgnoreCase) && string.Equals(HealthStatus, "Healthy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A Kyverno policy and whether it blocks.</summary>
/// <param name="Kind">Policy kind, for example <c>ValidatingPolicy</c>.</param>
/// <param name="Name">Policy name.</param>
/// <param name="ValidationActions">For example <c>Deny</c> or <c>Audit</c> (a ClusterPolicy's failure action).</param>
/// <param name="Ready">Whether Kyverno reports it ready.</param>
public sealed record KyvernoPolicyStatus(string Kind, string Name, IReadOnlyList<string> ValidationActions, bool? Ready);

/// <summary>Sync state of an ExternalSecret.</summary>
/// <param name="Namespace">Namespace.</param>
/// <param name="Name">Name.</param>
/// <param name="Ready"><c>true</c> when the Ready condition is True.</param>
/// <param name="Reason">Reason of the Ready condition.</param>
/// <param name="Message">Message of the Ready condition.</param>
/// <param name="RefreshTime">Last refresh.</param>
public sealed record ExternalSecretStatus(string Namespace, string Name, bool Ready, string? Reason, string? Message, DateTimeOffset? RefreshTime);

/// <summary>A Kubernetes Event about one object.</summary>
/// <param name="Type">Normal or Warning.</param>
/// <param name="Reason">Short machine-readable reason, for example <c>UpdateFailed</c>.</param>
/// <param name="Message">Human-readable message; controllers put details here that they keep out of status.</param>
/// <param name="LastSeen">When it was last recorded, if known.</param>
public sealed record KubernetesEvent(string? Type, string? Reason, string? Message, DateTimeOffset? LastSeen);

/// <summary>Outcome of a pod creation, which admission control may reject.</summary>
/// <param name="Created"><c>true</c> when the API server accepted the pod.</param>
/// <param name="Name">Name of the pod.</param>
/// <param name="StatusCode">HTTP status of a rejection.</param>
/// <param name="Message">Rejection message, for example from an admission policy.</param>
public sealed record PodCreationResult(bool Created, string Name, int? StatusCode, string? Message);

/// <summary>API server address and CA of a cluster, read from its kubeconfig.</summary>
/// <param name="ClusterName">Cluster entry name.</param>
/// <param name="Server">API server URL.</param>
/// <param name="CertificateAuthorityPem">Cluster CA certificates in PEM form, if present.</param>
public sealed record KubernetesEndpoint(string ClusterName, string Server, string? CertificateAuthorityPem);
