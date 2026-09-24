# App cluster aks-platform-<tier> in rg-platform-<tier>-aks (ADR-IR34 "Clusters and capacity", §7.0).
#   - AKS Free tier (no uptime SLA; the platform sleeps anyway, ADR-IR33).
#   - Pre-created identities from the foundation: id-aks-<tier>-controlplane (Network Contributor on
#     rg-platform-<tier>-shared, Managed Identity Operator on the kubelet identity, Contributor on
#     rg-platform-<tier>-data for the disk CSI driver) and id-aks-<tier>-kubelet (AcrPull on the shared
#     registry). AKS therefore needs no role assignment at creation (Q17).
#   - Node resource group named explicitly: rg-platform-<tier>-aks-nodes. Budgets filter by that name.
#   - Entra ID with Azure RBAC; local accounts off; workload identity and the OIDC issuer on.
#   - Pools: `system` (platform add-ons; tainted CriticalAddonsOnly, fixed at one node) and `apps` (autoscaled,
#     maximum 7 in nonprod and 4 in prod). Both use Standard_D4ds_v5 with ephemeral OS disks on the temporary
#     disk [VERIFY through stop and start, Q39; fallback: managed OS disks, os_disk_type = "Managed"].
#   - Automatic upgrades off: no automatic_upgrade_channel and node_os_upgrade_channel "None". Upgrades are
#     manual (kubernetes_version by pull request, then env-apply) with one surge node, while the builds pool of
#     aks-platform-build is at zero, so the surge fits the 65-vCPU quota (ADR-IR34 decision 9).
#   - No node auto-provisioning, so the cluster can be stopped (E42). env-sleep and env-wake own the power state.
#   - Run command on: `az aks command invoke`, run by the lifecycle identity or the provisioner, is the documented
#     fallback for operator sessions behind the TLS-re-terminating proxy (ADR-IR34 test harness). It still
#     authenticates with Entra ID and Azure RBAC.
#   - Azure CNI overlay with Cilium enforces the tenant NetworkPolicies; outbound through the load balancer with
#     the static egress IP pip-platform-<tier>-egress (network.tf).
#   - No availability zones on the pools: the database disks are zone-less LRS disks (terraform/apps/tier), and
#     zone-less VMs can attach them after any rebuild.

data "azurerm_user_assigned_identity" "controlplane" {
  name                = "id-aks-${var.tier}-controlplane"
  resource_group_name = local.rg_aks
}

data "azurerm_user_assigned_identity" "kubelet" {
  name                = "id-aks-${var.tier}-kubelet"
  resource_group_name = local.rg_aks
}

resource "azurerm_kubernetes_cluster" "this" {
  name                = local.cluster_name
  location            = var.location
  resource_group_name = local.rg_aks
  node_resource_group = local.rg_nodes
  dns_prefix          = local.cluster_name
  sku_tier            = "Free"
  kubernetes_version  = var.kubernetes_version

  # Automatic upgrades off (no automatic_upgrade_channel); no automatic node-image or OS patching either.
  node_os_upgrade_channel = "None"

  oidc_issuer_enabled               = true
  workload_identity_enabled         = true
  local_account_disabled            = true
  role_based_access_control_enabled = true
  run_command_enabled               = true
  image_cleaner_enabled             = true
  image_cleaner_interval_hours      = 48

  # azurerm 5.x requires the block; Manual means no node auto-provisioning.
  node_provisioning_profile {
    mode = "Manual"
  }

  azure_active_directory_role_based_access_control {
    tenant_id          = var.tenant_id
    azure_rbac_enabled = true
    # No Kubernetes-level admin group: cluster rights come only from Azure role assignments made by the
    # foundation (the lifecycle identity, platform-operators and the conformance principal).
    admin_group_object_ids = []
  }

  dynamic "api_server_access_profile" {
    for_each = length(var.api_server_authorized_ip_ranges) > 0 ? [1] : []

    content {
      authorized_ip_ranges = var.api_server_authorized_ip_ranges
    }
  }

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.controlplane.id]
  }

  kubelet_identity {
    client_id                 = data.azurerm_user_assigned_identity.kubelet.client_id
    object_id                 = data.azurerm_user_assigned_identity.kubelet.principal_id
    user_assigned_identity_id = data.azurerm_user_assigned_identity.kubelet.id
  }

  default_node_pool {
    name                         = "system"
    vm_size                      = var.system_node_pool.vm_size
    vnet_subnet_id               = azurerm_subnet.aks.id
    auto_scaling_enabled         = false
    node_count                   = var.system_node_pool.node_count
    only_critical_addons_enabled = true
    orchestrator_version         = var.kubernetes_version
    os_sku                       = "AzureLinux"
    os_disk_type                 = "Ephemeral"
    os_disk_size_gb              = var.os_disk_size_gb
    temporary_name_for_rotation  = "systemtmp"

    upgrade_settings {
      max_surge = "1"
    }

    tags = merge(local.base_tags, { "platform-component" = "cluster" })
  }

  network_profile {
    network_plugin      = "azure"
    network_plugin_mode = "overlay"
    network_data_plane  = "cilium"
    network_policy      = "cilium"
    pod_cidr            = var.cluster_network.pod_cidr
    service_cidr        = var.cluster_network.service_cidr
    dns_service_ip      = var.cluster_network.dns_service_ip
    outbound_type       = "loadBalancer"
    load_balancer_sku   = "standard"

    load_balancer_profile {
      outbound_ip_address_ids = [azurerm_public_ip.egress.id]
    }
  }

  # AKS applies the cluster's tags to the resources it creates in rg-platform-<tier>-aks-nodes [VERIFY for
  # disks that Kubernetes creates; the StorageClass tags parameter covers dynamic disks].
  tags = merge(local.base_tags, { "platform-component" = "cluster" })

  lifecycle {
    # After a start the node count can sit outside the configured value for a while.
    ignore_changes = [default_node_pool[0].node_count]
  }
}

resource "azurerm_kubernetes_cluster_node_pool" "apps" {
  name                  = "apps"
  kubernetes_cluster_id = azurerm_kubernetes_cluster.this.id
  mode                  = "User"
  vm_size               = var.apps_node_pool.vm_size
  vnet_subnet_id        = azurerm_subnet.aks.id
  auto_scaling_enabled  = true
  min_count             = var.apps_node_pool.min_count
  max_count             = var.apps_node_pool.max_count
  orchestrator_version  = var.kubernetes_version
  os_sku                = "AzureLinux"
  os_disk_type          = "Ephemeral"
  os_disk_size_gb       = var.os_disk_size_gb

  upgrade_settings {
    max_surge = "1"
  }

  tags = merge(local.base_tags, { "platform-component" = "cluster" })

  lifecycle {
    # The autoscaler owns the node count.
    ignore_changes = [node_count]
  }
}

# Control-plane audit to log-platform-<tier>: who changed what in the cluster, including every break-glass
# action (docs/runbooks/break-glass.md).
resource "azurerm_monitor_diagnostic_setting" "aks" {
  name                           = "aks-audit-to-log-platform"
  target_resource_id             = azurerm_kubernetes_cluster.this.id
  log_analytics_workspace_id     = azurerm_log_analytics_workspace.this.id
  log_analytics_destination_type = "Dedicated" # resource-specific tables AKSAuditAdmin, AKSControlPlane

  # kube-audit-admin excludes get and list events, which keeps ingestion affordable.
  enabled_log {
    category = "kube-audit-admin"
  }

  enabled_log {
    category = "guard"
  }
}
