namespace Platform.Conformance.Harness.Clients;

/// <summary>
/// Read access to the platform's Azure resources through Azure Resource Manager, plus registry metadata through the
/// ACR data plane (Entra token exchanged at <c>/oauth2/exchange</c>, then <c>/oauth2/token</c>).
/// </summary>
public interface IAzureApi
{
    /// <summary>Gets the configured subscription.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AzureSubscriptionInfo> GetSubscriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the power and provisioning state of an AKS cluster, with its node pools.</summary>
    /// <param name="resourceGroup">Resource group of the cluster.</param>
    /// <param name="clusterName">Cluster name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AksClusterState> GetClusterStateAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken = default);

    /// <summary>Gets the node pools of an AKS cluster with their node counts.</summary>
    /// <param name="resourceGroup">Resource group of the cluster.</param>
    /// <param name="clusterName">Cluster name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<AksAgentPoolState>> GetAgentPoolsAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken = default);

    /// <summary>Gets the user kubeconfig of an AKS cluster (<c>listClusterUserCredential</c>); used for the server address and CA only.</summary>
    /// <param name="resourceGroup">Resource group of the cluster.</param>
    /// <param name="clusterName">Cluster name.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<byte[]> GetClusterUserKubeconfigAsync(string resourceGroup, string clusterName, CancellationToken cancellationToken = default);

    /// <summary>Lists the subscription's resource groups with their tags.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<AzureResourceGroupInfo>> ListResourceGroupsAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the managed disks in a resource group (generic ARM reads of <c>Microsoft.Compute/disks</c>).</summary>
    /// <param name="resourceGroup">Resource group.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<AzureManagedDisk>> ListManagedDisksAsync(string resourceGroup, CancellationToken cancellationToken = default);

    /// <summary>Gets whether an alert processing rule is enabled (generic ARM read of <c>Microsoft.AlertsManagement/actionRules</c>).</summary>
    /// <param name="resourceGroup">Resource group of the rule.</param>
    /// <param name="ruleName">Rule name, for example <c>apr-sleep-nonprod</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AlertProcessingRuleState> GetAlertProcessingRuleAsync(string resourceGroup, string ruleName, CancellationToken cancellationToken = default);

    /// <summary>Gets the attributes of a registry repository (<c>GET /acr/v1/{repository}</c>).</summary>
    /// <param name="repository">Repository, for example <c>workorders/ui-server</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AcrRepositoryAttributes> GetRegistryRepositoryAsync(string repository, CancellationToken cancellationToken = default);

    /// <summary>Gets the attributes of a registry tag (<c>GET /acr/v1/{repository}/_tags/{tag}</c>).</summary>
    /// <param name="repository">Repository, for example <c>workorders/ui-server</c>.</param>
    /// <param name="tag">Tag, for example <c>2.5.120</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<AcrTagAttributes> GetRegistryTagAsync(string repository, string tag, CancellationToken cancellationToken = default);
}
