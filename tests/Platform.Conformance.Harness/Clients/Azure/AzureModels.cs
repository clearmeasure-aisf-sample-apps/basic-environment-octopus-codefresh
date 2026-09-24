namespace Platform.Conformance.Harness.Clients;

/// <summary>Where the Azure client points.</summary>
public sealed record AzureApiOptions
{
    /// <summary>Subscription ID.</summary>
    public required string SubscriptionId { get; init; }

    /// <summary>Entra tenant ID; sent with the registry token exchange when known.</summary>
    public string? TenantId { get; init; }

    /// <summary>Registry login server, for example <c>myregistry.azurecr.io</c>; registry calls need it.</summary>
    public string? RegistryLoginServer { get; init; }

    /// <summary>Timeout of registry HTTP requests.</summary>
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(100);
}

/// <summary>An Azure subscription.</summary>
/// <param name="SubscriptionId">Subscription ID.</param>
/// <param name="DisplayName">Display name.</param>
/// <param name="State">State, for example <c>Enabled</c>.</param>
/// <param name="TenantId">Tenant ID.</param>
public sealed record AzureSubscriptionInfo(string SubscriptionId, string? DisplayName, string? State, string? TenantId);

/// <summary>Power and provisioning state of an AKS cluster.</summary>
/// <param name="Name">Cluster name.</param>
/// <param name="ResourceGroup">Resource group.</param>
/// <param name="PowerState"><c>Running</c> or <c>Stopped</c>.</param>
/// <param name="ProvisioningState">For example <c>Succeeded</c>, <c>Starting</c> or <c>Stopping</c>.</param>
/// <param name="KubernetesVersion">Current Kubernetes version.</param>
/// <param name="Fqdn">API server FQDN (public or private).</param>
/// <param name="LocalAccountsDisabled"><c>true</c> when local accounts are disabled.</param>
/// <param name="AzureRbacEnabled"><c>true</c> when Kubernetes authorization uses Azure RBAC.</param>
/// <param name="AgentPools">Node pools.</param>
public sealed record AksClusterState(
    string Name,
    string ResourceGroup,
    string? PowerState,
    string? ProvisioningState,
    string? KubernetesVersion,
    string? Fqdn,
    bool? LocalAccountsDisabled,
    bool? AzureRbacEnabled,
    IReadOnlyList<AksAgentPoolState> AgentPools)
{
    /// <summary><c>true</c> when the power state is <c>Running</c>.</summary>
    public bool IsRunning => string.Equals(PowerState, "Running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compact summary for messages.</summary>
    public override string ToString() =>
        $"{Name} power={PowerState} provisioning={ProvisioningState} pools=[{string.Join(", ", AgentPools.Select(pool => $"{pool.Name}:{pool.Count}"))}]";
}

/// <summary>Node counts and state of an AKS node pool.</summary>
/// <param name="Name">Pool name.</param>
/// <param name="Mode"><c>System</c> or <c>User</c>.</param>
/// <param name="Count">Current node count.</param>
/// <param name="MinCount">Autoscaler minimum.</param>
/// <param name="MaxCount">Autoscaler maximum.</param>
/// <param name="AutoScaling"><c>true</c> when the autoscaler is on.</param>
/// <param name="PowerState"><c>Running</c> or <c>Stopped</c>.</param>
/// <param name="ProvisioningState">Provisioning state.</param>
/// <param name="VmSize">VM size.</param>
public sealed record AksAgentPoolState(string Name, string? Mode, int? Count, int? MinCount, int? MaxCount, bool? AutoScaling, string? PowerState, string? ProvisioningState, string? VmSize);

/// <summary>A resource group and its tags.</summary>
/// <param name="Name">Name.</param>
/// <param name="Location">Region.</param>
/// <param name="ProvisioningState">Provisioning state.</param>
/// <param name="Tags">Tags.</param>
public sealed record AzureResourceGroupInfo(string Name, string? Location, string? ProvisioningState, IReadOnlyDictionary<string, string> Tags);

/// <summary>A managed disk.</summary>
/// <param name="Name">Disk name.</param>
/// <param name="Id">Resource ID.</param>
/// <param name="Location">Region.</param>
/// <param name="ManagedBy">Resource that uses it (a VM), or <c>null</c> when unattached.</param>
/// <param name="DiskState">For example <c>Attached</c>, <c>Unattached</c> or <c>Reserved</c>.</param>
/// <param name="SizeGb">Size in GiB.</param>
/// <param name="Sku">SKU name.</param>
/// <param name="Tags">Tags.</param>
public sealed record AzureManagedDisk(string Name, string Id, string? Location, string? ManagedBy, string? DiskState, int? SizeGb, string? Sku, IReadOnlyDictionary<string, string> Tags);

/// <summary>An alert processing rule (<c>Microsoft.AlertsManagement/actionRules</c>).</summary>
/// <param name="Name">Rule name, for example <c>apr-sleep-nonprod</c>.</param>
/// <param name="ResourceGroup">Resource group.</param>
/// <param name="Enabled"><c>true</c> when the rule is enabled.</param>
/// <param name="Description">Description.</param>
public sealed record AlertProcessingRuleState(string Name, string ResourceGroup, bool Enabled, string? Description);

/// <summary>Changeable attributes of a registry repository, tag or manifest.</summary>
/// <param name="DeleteEnabled">Delete allowed.</param>
/// <param name="WriteEnabled">Overwrite allowed (<c>false</c> for a locked image).</param>
/// <param name="ReadEnabled">Pull allowed.</param>
/// <param name="ListEnabled">Listing allowed.</param>
public sealed record AcrChangeableAttributes(bool? DeleteEnabled, bool? WriteEnabled, bool? ReadEnabled, bool? ListEnabled);

/// <summary>Attributes of a registry repository.</summary>
/// <param name="Repository">Repository name, for example <c>workorders/ui-server</c>.</param>
/// <param name="TagCount">Number of tags.</param>
/// <param name="ManifestCount">Number of manifests.</param>
/// <param name="LastUpdateTime">Last change.</param>
/// <param name="Attributes">Changeable attributes.</param>
public sealed record AcrRepositoryAttributes(string Repository, int? TagCount, int? ManifestCount, DateTimeOffset? LastUpdateTime, AcrChangeableAttributes Attributes);

/// <summary>Attributes of a registry tag.</summary>
/// <param name="Repository">Repository name.</param>
/// <param name="Tag">Tag.</param>
/// <param name="Digest">Manifest digest.</param>
/// <param name="CreatedTime">Creation time.</param>
/// <param name="LastUpdateTime">Last change.</param>
/// <param name="Signed">Whether the registry reports it signed.</param>
/// <param name="Attributes">Changeable attributes.</param>
public sealed record AcrTagAttributes(string Repository, string Tag, string? Digest, DateTimeOffset? CreatedTime, DateTimeOffset? LastUpdateTime, bool? Signed, AcrChangeableAttributes Attributes);
