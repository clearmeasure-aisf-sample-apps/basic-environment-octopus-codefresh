# Outputs for the runner install (P1-04), the Codefresh runtime, the conformance tests (CAP-CF-001 to
# CAP-CF-003, CAP-CF-012, CAP-AZ-015) and the inventory. None is secret: with local accounts disabled
# the cluster exposes no admin credential.

output "cluster_name" {
  description = "aks-platform-build; also the Codefresh runtime context (<cf-runtime> = aks-platform-build/codefresh)."
  value       = azurerm_kubernetes_cluster.build.name
}

output "cluster_id" {
  description = "Resource ID of the cluster."
  value       = azurerm_kubernetes_cluster.build.id
}

output "resource_group_name" {
  description = "rg-platform-build."
  value       = azurerm_kubernetes_cluster.build.resource_group_name
}

output "node_resource_group" {
  description = "rg-platform-build-aks-nodes: nodes, load balancer and egress IP. The budget budget-platform-build filters it by name."
  value       = azurerm_kubernetes_cluster.build.node_resource_group
}

output "fqdn" {
  description = "API server host name."
  value       = azurerm_kubernetes_cluster.build.fqdn
}

output "oidc_issuer_url" {
  description = "Cluster OIDC issuer. No identity federates to it: builds get no cloud identity (TB2)."
  value       = azurerm_kubernetes_cluster.build.oidc_issuer_url
}

output "kubelet_identity" {
  description = "AKS-created kubelet identity in the node group. It holds no role outside that group: step images come through the cf-platform-pull token, not a registry grant (CAP-CF-012)."
  # The block is computed: AKS fills it at creation.
  value = {
    object_id                 = try(azurerm_kubernetes_cluster.build.kubelet_identity[0].object_id, null)
    client_id                 = try(azurerm_kubernetes_cluster.build.kubelet_identity[0].client_id, null)
    user_assigned_identity_id = try(azurerm_kubernetes_cluster.build.kubelet_identity[0].user_assigned_identity_id, null)
  }
}

output "control_plane_principal_id" {
  description = "Object ID of the system-assigned control-plane identity (CAP-CF-012 checks its role assignments)."
  value       = azurerm_kubernetes_cluster.build.identity[0].principal_id
}

output "runner" {
  description = "Values for codefresh/runner/values.yaml and the Codefresh runtime."
  value = {
    kube_context   = azurerm_kubernetes_cluster.build.name
    namespace      = "codefresh"
    runtime        = "${azurerm_kubernetes_cluster.build.name}/codefresh"
    system_pool    = azurerm_kubernetes_cluster.build.default_node_pool[0].name
    builds_pool    = azurerm_kubernetes_cluster_node_pool.builds.name
    builds_taint   = local.builds_taint
    builds_labels  = local.builds_labels
    builds_min_max = [azurerm_kubernetes_cluster_node_pool.builds.min_count, azurerm_kubernetes_cluster_node_pool.builds.max_count]
  }
}

output "backend" {
  description = "State of this layer: container tfstate of the global state account (terraform/foundation output tfstate.global.storage_account_name)."
  value = {
    container_name = "tfstate"
    key            = "build.tfstate"
  }
}
