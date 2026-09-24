# aks-platform-build in rg-platform-build; nodes, load balancer and egress IP in
# rg-platform-build-aks-nodes (ADR-IR34 "Resource groups", "Clusters and capacity", "Build runner").

locals {
  cluster_name        = "aks-platform-build"
  resource_group_name = "rg-platform-build"
  node_resource_group = "rg-platform-build-aks-nodes"

  # The taint and label that codefresh/runner/values.yaml relies on; AKS also labels every node with
  # kubernetes.azure.com/agentpool = <pool name>, which the runner values select.
  builds_taint  = "codefresh.io/builds=true:NoSchedule"
  builds_labels = { "pool" = "builds" }
  system_labels = { "pool" = "system" }

  tags = merge(var.tags, {
    "platform-tier"      = "build"
    "platform-component" = "build-cluster"
    "managed-by"         = "terraform-build"
  })
}

# Created by terraform/foundation; this layer never creates or changes it.
data "azurerm_resource_group" "build" {
  name = local.resource_group_name
}

resource "azurerm_kubernetes_cluster" "build" {
  name                = local.cluster_name
  location            = data.azurerm_resource_group.build.location
  resource_group_name = data.azurerm_resource_group.build.name
  node_resource_group = local.node_resource_group
  dns_prefix          = local.cluster_name
  sku_tier            = "Free"
  kubernetes_version  = var.kubernetes_version

  # Automatic upgrades off (ADR-IR34): no upgrade channel (AKS "none") and no node-image channel. Upgrades
  # and node-image updates are manual, while the builds pool is at zero.
  node_os_upgrade_channel = "None"

  # Entra ID with Azure RBAC only; no local accounts, so no client certificate works (CAP-AZ-015).
  local_account_disabled            = true
  role_based_access_control_enabled = true

  azure_active_directory_role_based_access_control {
    tenant_id          = var.tenant_id
    azure_rbac_enabled = true
    # Cluster rights come only from Azure role assignments (terraform/foundation).
    admin_group_object_ids = []
  }

  # Builds get no cloud identity (TB2); the issuer is published for inventory and tests only.
  oidc_issuer_enabled       = true
  workload_identity_enabled = false

  # Fallback for sessions behind a TLS-re-terminating proxy: az aks command invoke (ADR-IR34 "Test
  # harness", authentication).
  run_command_enabled = true

  # System-assigned identities; AKS grants them what they need inside the node group only.
  identity {
    type = "SystemAssigned"
  }

  # azurerm 5.x requires the block; Manual means no node auto-provisioning (the autoscaler scales builds).
  node_provisioning_profile {
    mode = "Manual"
  }

  default_node_pool {
    name                        = "system"
    vm_size                     = var.system_pool.vm_size
    node_count                  = var.system_pool.node_count
    auto_scaling_enabled        = false
    orchestrator_version        = var.kubernetes_version
    os_sku                      = "Ubuntu"
    os_disk_type                = "Managed"
    os_disk_size_gb             = var.system_pool.os_disk_size_gb
    node_labels                 = local.system_labels
    temporary_name_for_rotation = "systemtmp"
    tags                        = local.tags

    upgrade_settings {
      max_surge = "10%"
    }
  }

  # Cluster-wide; only the builds pool autoscales. Scale down once a node has been unneeded for
  # scale_down_unneeded, also right after a scale-up; nodes with local (dind) volumes are removable.
  auto_scaler_profile {
    scale_down_unneeded           = var.scale_down_unneeded
    scale_down_delay_after_add    = var.scale_down_unneeded
    skip_nodes_with_local_storage = false
  }

  # AKS-managed VNet with Azure CNI overlay; outbound through the managed load balancer and its public IP
  # in the node group.
  network_profile {
    network_plugin      = "azure"
    network_plugin_mode = "overlay"
    load_balancer_sku   = "standard"
    outbound_type       = "loadBalancer"
  }

  dynamic "api_server_access_profile" {
    for_each = length(var.api_server_authorized_ip_ranges) > 0 ? [1] : []

    content {
      authorized_ip_ranges = var.api_server_authorized_ip_ranges
    }
  }

  tags = local.tags
}

resource "azurerm_kubernetes_cluster_node_pool" "builds" {
  name                  = "builds"
  kubernetes_cluster_id = azurerm_kubernetes_cluster.build.id
  mode                  = "User"
  vm_size               = var.builds_pool.vm_size
  auto_scaling_enabled  = true
  min_count             = var.builds_pool.min_count
  max_count             = var.builds_pool.max_count
  node_count            = var.builds_pool.min_count
  orchestrator_version  = var.kubernetes_version
  os_sku                = "Ubuntu"
  os_disk_type          = var.builds_pool.os_disk_type
  # AKS reserves the lesser of 20 MB per possible pod + 50 MB and 25 % of memory for the kubelet. With the Azure
  # CNI overlay default of 250 pods that is 4 GiB of a Standard_D4as_v6, which leaves about 11.5 GiB allocatable:
  # less than the dind pod (11 GiB request, codefresh/runner/values.yaml), the engine and the AKS daemon sets, so
  # the autoscaler would never place a build. 30 pods reserve 650 MB; a build node runs one engine, one dind pod
  # and the daemon sets.
  max_pods = 30
  # A vm_size or disk change (Q40 fallback Standard_D8as_v6) cycles the pool through a temporary one.
  temporary_name_for_rotation = "buildstmp"
  node_labels                 = local.builds_labels
  node_taints                 = [local.builds_taint]
  tags                        = local.tags

  upgrade_settings {
    max_surge = "10%"
  }

  lifecycle {
    # The autoscaler owns the node count.
    ignore_changes = [node_count]
  }
}
