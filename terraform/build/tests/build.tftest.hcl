# Offline tests of terraform/build with a mocked provider: no Azure call.
#   cd terraform/build && terraform init -backend=false && terraform test
# They prove the ADR-IR34 cluster shape (Free tier, Entra ID with Azure RBAC, no local accounts, no
# automatic upgrades, node group name) and the two pools the runner values rely on.

mock_provider "azurerm" {
  mock_data "azurerm_resource_group" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-build"
      location = "southcentralus"
    }
  }

  mock_resource "azurerm_kubernetes_cluster" {
    defaults = {
      id                  = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-build/providers/Microsoft.ContainerService/managedClusters/aks-platform-build"
      oidc_issuer_url     = "https://southcentralus.oic.prod-aks.azure.com/00000000-0000-0000-0000-000000000002/00000000-0000-0000-0000-000000000003/"
      fqdn                = "aks-platform-build-00000000.hcp.southcentralus.azmk8s.io"
      node_resource_group = "rg-platform-build-aks-nodes"
    }
  }
}

variables {
  tenant_id       = "00000000-0000-0000-0000-000000000002"
  subscription_id = "00000000-0000-0000-0000-000000000001"
}

run "cluster_matches_adr_ir34" {
  command = plan

  assert {
    condition     = azurerm_kubernetes_cluster.build.name == "aks-platform-build" && azurerm_kubernetes_cluster.build.resource_group_name == "rg-platform-build" && azurerm_kubernetes_cluster.build.node_resource_group == "rg-platform-build-aks-nodes"
    error_message = "Cluster name or resource groups differ from §7.0."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.sku_tier == "Free" && azurerm_kubernetes_cluster.build.local_account_disabled && azurerm_kubernetes_cluster.build.role_based_access_control_enabled
    error_message = "The cluster must be Free tier with local accounts disabled."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.azure_active_directory_role_based_access_control[0].azure_rbac_enabled
    error_message = "Entra ID with Azure RBAC is required."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.automatic_upgrade_channel == null && azurerm_kubernetes_cluster.build.node_os_upgrade_channel == "None"
    error_message = "Automatic upgrades must be off."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.identity[0].type == "SystemAssigned" && !azurerm_kubernetes_cluster.build.workload_identity_enabled
    error_message = "System-assigned identity and no workload identity."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.default_node_pool[0].name == "system" && azurerm_kubernetes_cluster.build.default_node_pool[0].vm_size == "Standard_B2s" && azurerm_kubernetes_cluster.build.default_node_pool[0].node_count == 1 && !azurerm_kubernetes_cluster.build.default_node_pool[0].auto_scaling_enabled
    error_message = "Pool system must be one always-on Standard_B2s."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.default_node_pool[0].only_critical_addons_enabled != true
    error_message = "Pool system must stay untainted: the runner agent runs there."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.builds.name == "builds" && azurerm_kubernetes_cluster_node_pool.builds.vm_size == "Standard_D4ds_v5" && azurerm_kubernetes_cluster_node_pool.builds.auto_scaling_enabled && azurerm_kubernetes_cluster_node_pool.builds.min_count == 0 && azurerm_kubernetes_cluster_node_pool.builds.max_count == 2
    error_message = "Pool builds must be Standard_D4ds_v5, autoscaled 0 to 2."
  }

  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.builds.node_taints == tolist(["codefresh.io/builds=true:NoSchedule"]) && azurerm_kubernetes_cluster_node_pool.builds.node_labels["pool"] == "builds"
    error_message = "Pool builds needs the taint codefresh.io/builds=true:NoSchedule and label pool=builds."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.auto_scaler_profile[0].scale_down_unneeded == "10m" && !azurerm_kubernetes_cluster.build.auto_scaler_profile[0].skip_nodes_with_local_storage
    error_message = "The autoscaler must remove a builds node after 10 idle minutes."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.network_profile[0].network_plugin == "azure" && azurerm_kubernetes_cluster.build.network_profile[0].network_plugin_mode == "overlay" && azurerm_kubernetes_cluster.build.default_node_pool[0].vnet_subnet_id == null
    error_message = "AKS-managed VNet with Azure CNI overlay expected."
  }

  assert {
    condition     = azurerm_kubernetes_cluster.build.tags["platform-tier"] == "build" && azurerm_kubernetes_cluster.build.tags["platform-component"] == "build-cluster" && azurerm_kubernetes_cluster_node_pool.builds.tags["platform-tier"] == "build"
    error_message = "Cost tags missing."
  }
}

run "outputs_after_apply" {
  command = apply

  assert {
    condition     = output.node_resource_group == "rg-platform-build-aks-nodes" && output.cluster_name == "aks-platform-build" && contains(keys(output.kubelet_identity), "object_id")
    error_message = "Outputs must expose the node group, the cluster and the kubelet identity (AKS fills the identity block; mocks leave it empty)."
  }

  assert {
    condition     = output.runner.runtime == "aks-platform-build/codefresh" && output.runner.builds_taint == "codefresh.io/builds=true:NoSchedule"
    error_message = "Runner outputs differ from §7.0."
  }
}

run "rejects_a_builds_pool_that_cannot_reach_zero" {
  command = plan

  variables {
    builds_pool = {
      vm_size      = "Standard_D4ds_v5"
      min_count    = 1
      max_count    = 2
      os_disk_type = "Ephemeral"
    }
  }

  expect_failures = [var.builds_pool]
}
