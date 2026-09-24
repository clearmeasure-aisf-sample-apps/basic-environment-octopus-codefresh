# Offline test of terraform/tier with mocked providers: nothing reaches Azure, Entra or a cluster.
# Run: terraform -chdir=terraform/tier init -backend=false && terraform -chdir=terraform/tier test
# The Argo CD bootstrap files are read from argocd/bootstrap/ of the same checkout.

mock_provider "azurerm" {
  mock_data "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-aks/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-mock"
      client_id    = "55555555-5555-5555-5555-555555555555"
      principal_id = "66666666-6666-6666-6666-666666666666"
    }
  }

  # Resource IDs that other arguments validate need a well-formed default.
  mock_resource "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.OperationalInsights/workspaces/log-platform-nonprod"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-aks/providers/Microsoft.KeyVault/vaults/kv-platform-np-test"
    }
  }

  mock_resource "azurerm_subnet" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.Network/virtualNetworks/vnet-platform-nonprod/subnets/snet-aks-nonprod"
    }
  }

  mock_resource "azurerm_kubernetes_cluster" {
    defaults = {
      id                  = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-aks/providers/Microsoft.ContainerService/managedClusters/aks-platform-nonprod"
      oidc_issuer_url     = "https://southcentralus.oic.prod-aks.azure.com/11111111-1111-1111-1111-111111111111/cccccccc-cccc-cccc-cccc-cccccccccccc/"
      node_resource_group = "rg-platform-nonprod-aks-nodes"
      kube_config = [{
        host                   = "https://aks-platform-nonprod.example.invalid:443"
        cluster_ca_certificate = "Y2E="
        client_certificate     = ""
        client_key             = ""
        username               = "clusterUser"
        password               = ""
      }]
    }
  }

  mock_resource "azurerm_public_ip" {
    defaults = {
      id         = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.Network/publicIPAddresses/pip-platform-nonprod-egress"
      ip_address = "20.30.40.50"
    }
  }
}

mock_provider "helm" {}
mock_provider "kubernetes" {}

variables {
  tier                         = "nonprod"
  tenant_id                    = "11111111-1111-1111-1111-111111111111"
  subscription_id              = "22222222-2222-2222-2222-222222222222"
  location                     = "southcentralus"
  platform_key_vault_name      = "kv-platform-np-test"
  network                      = { address_space = ["10.10.0.0/16"], aks_subnet_prefix = "10.10.0.0/22" }
  apps_node_pool               = { vm_size = "Standard_D4ds_v5", min_count = 1, max_count = 7 }
  octopus_url                  = "https://example.octopus.app"
  octopus_space                = "Test Space"
  octopus_worker_chart_version = "3.15.1"
  argocd_chart_version         = "10.9.2"
  argocd_apps_chart_version    = "2.0.5"
  env_repo_url                 = "https://github.com/example/env.git"
}

run "nonprod_cluster" {
  command = apply

  assert {
    condition     = azurerm_kubernetes_cluster.this.name == "aks-platform-nonprod" && azurerm_kubernetes_cluster.this.resource_group_name == "rg-platform-nonprod-aks"
    error_message = "cluster name and group must follow §7.0"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.node_resource_group == "rg-platform-nonprod-aks-nodes"
    error_message = "the node resource group is named explicitly"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.sku_tier == "Free" && azurerm_kubernetes_cluster.this.local_account_disabled && azurerm_kubernetes_cluster.this.azure_active_directory_role_based_access_control[0].azure_rbac_enabled
    error_message = "Free tier, Entra ID with Azure RBAC, local accounts off"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.automatic_upgrade_channel == null && azurerm_kubernetes_cluster.this.node_os_upgrade_channel == "None"
    error_message = "automatic upgrades are off"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.default_node_pool[0].only_critical_addons_enabled && !azurerm_kubernetes_cluster.this.default_node_pool[0].auto_scaling_enabled && azurerm_kubernetes_cluster.this.default_node_pool[0].node_count == 1
    error_message = "the system pool is tainted and fixed at one node"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.default_node_pool[0].os_disk_type == "Ephemeral" && azurerm_kubernetes_cluster_node_pool.apps.os_disk_type == "Ephemeral"
    error_message = "both pools use ephemeral OS disks"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.default_node_pool[0].vm_size == "Standard_D4ds_v5" && azurerm_kubernetes_cluster_node_pool.apps.vm_size == "Standard_D4ds_v5"
    error_message = "both pools use Standard_D4ds_v5"
  }
  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.apps.max_count == 7 && azurerm_kubernetes_cluster_node_pool.apps.auto_scaling_enabled
    error_message = "the nonprod apps pool autoscales up to 7"
  }
  assert {
    condition     = azurerm_kubernetes_cluster.this.network_profile[0].load_balancer_profile[0].outbound_ip_address_ids == toset([azurerm_public_ip.egress.id])
    error_message = "outbound traffic leaves through pip-platform-nonprod-egress"
  }
  assert {
    condition     = azurerm_virtual_network.this.name == "vnet-platform-nonprod" && azurerm_subnet.aks.name == "snet-aks-nonprod" && azurerm_virtual_network.this.resource_group_name == "rg-platform-nonprod-shared"
    error_message = "network names follow §7.0"
  }
  assert {
    condition     = azurerm_public_ip.egress.name == "pip-platform-nonprod-egress" && azurerm_public_ip.ingress.name == "pip-platform-nonprod-ingress"
    error_message = "public IP names follow §7.0"
  }
  assert {
    condition     = output.apps_domain == "20-30-40-50.sslip.io"
    error_message = "apps_domain is the dashed ingress IP under sslip.io"
  }
  assert {
    condition     = azurerm_log_analytics_workspace.this.name == "log-platform-nonprod" && azurerm_key_vault.platform.resource_group_name == "rg-platform-nonprod-aks"
    error_message = "workspace and platform vault follow §7.0"
  }
  assert {
    condition     = alltrue([for k in ["platform-tier", "platform-component"] : contains(keys(azurerm_kubernetes_cluster.this.tags), k)]) && azurerm_kubernetes_cluster.this.tags["platform-tier"] == "nonprod"
    error_message = "platform resources carry the cost tags"
  }
}

run "nonprod_sleep_rule_and_federation" {
  command = apply

  assert {
    condition     = azurerm_monitor_alert_processing_rule_suppression.sleep.name == "apr-sleep-nonprod" && azurerm_monitor_alert_processing_rule_suppression.sleep.resource_group_name == "rg-platform-nonprod-aks"
    error_message = "the sleep rule is apr-sleep-<tier> in the cluster group"
  }
  assert {
    condition     = azurerm_monitor_alert_processing_rule_suppression.sleep.enabled == false
    error_message = "the sleep rule is created disabled"
  }
  assert {
    condition = toset(azurerm_monitor_alert_processing_rule_suppression.sleep.scopes) == toset([
      "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-aks",
      "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps",
    ])
    error_message = "the rule covers the tier's cluster and apps groups"
  }
  assert {
    condition     = azurerm_monitor_alert_processing_rule_suppression.sleep.condition[0].monitor_service[0].operator == "NotEquals" && length(azurerm_monitor_alert_processing_rule_suppression.sleep.condition[0].monitor_service[0].values) == 5
    error_message = "activity-log alerts are never suppressed"
  }
  assert {
    condition = toset([for c in azurerm_federated_identity_credential.platform_workload : c.subject]) == toset([
      "system:serviceaccount:external-secrets:external-secrets",
      "system:serviceaccount:kyverno:kyverno-admission-controller",
      "system:serviceaccount:platform-backup:db-backup",
    ])
    error_message = "the three platform workload subjects"
  }
  assert {
    condition     = alltrue([for c in azurerm_federated_identity_credential.platform_workload : c.issuer == azurerm_kubernetes_cluster.this.oidc_issuer_url && tolist(c.audience) == tolist(["api://AzureADTokenExchange"])])
    error_message = "credentials trust this cluster's issuer"
  }
  assert {
    condition     = toset(keys(helm_release.octopus_worker)) == toset(["tdd", "uat"])
    error_message = "one worker per nonprod environment"
  }
  assert {
    condition     = output.octopus_worker_script_service_accounts["tdd"] == "octopus-worker-tdd/octopus-worker-tdd-scripts"
    error_message = "script pods run as a fixed service account"
  }
}

run "prod_cluster" {
  command = apply

  variables {
    tier                    = "prod"
    platform_key_vault_name = "kv-platform-pr-test"
    network                 = { address_space = ["10.20.0.0/16"], aks_subnet_prefix = "10.20.0.0/22" }
    apps_node_pool          = { vm_size = "Standard_D4ds_v5", min_count = 1, max_count = 4 }
  }

  assert {
    condition     = azurerm_kubernetes_cluster.this.name == "aks-platform-prod" && azurerm_kubernetes_cluster.this.node_resource_group == "rg-platform-prod-aks-nodes"
    error_message = "prod names follow §7.0"
  }
  assert {
    condition     = azurerm_kubernetes_cluster_node_pool.apps.max_count == 4
    error_message = "the prod apps pool autoscales up to 4"
  }
  assert {
    condition     = toset(keys(helm_release.octopus_worker)) == toset(["prod"])
    error_message = "one worker for prod"
  }
  assert {
    condition     = azurerm_monitor_alert_processing_rule_suppression.sleep.name == "apr-sleep-prod"
    error_message = "prod sleep rule"
  }
}

run "nonprod_rejects_more_than_seven_apps_nodes" {
  command = plan

  variables {
    apps_node_pool = { vm_size = "Standard_D4ds_v5", min_count = 1, max_count = 8 }
  }

  expect_failures = [var.apps_node_pool]
}

run "prod_rejects_more_than_four_apps_nodes" {
  command = plan

  variables {
    tier           = "prod"
    apps_node_pool = { vm_size = "Standard_D4ds_v5", min_count = 1, max_count = 5 }
  }

  expect_failures = [var.apps_node_pool]
}
