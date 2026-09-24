# Workload identity of an app that declares azure.workloadIdentity (ADR-IR34 decision 11, §7.0 "Identities").
#
# terraform/apps/grants (the provisioner) creates id-<app>-<env>-app in rg-platform-<tier>-apps with its roles on
# rg-app-<app>-<tier>. This layer adds what depends on the cluster or the vault:
#   - the federated credential system:serviceaccount:<app>-<env>:<azure.serviceAccount>, issued by the tier
#     cluster's OIDC issuer. A rebuilt cluster has a new issuer, so apps-apply runs again after env-apply.
#   - vault key azure-client-id, which ESO maps to AZURE_CLIENT_ID; the tenant chart needs no copied client ID
#     [VERIFY webhook injection without the service-account annotation, Q44; fallback: a one-line pull request].
# Until grants has created the identity, both are skipped (an ARM listing finds no identity), so the first
# apps-apply of a new app succeeds and the second one completes it. One service account per identity, so no
# concurrent writes on one identity (limit 20, decision 14).

locals {
  app_identity_names = module.descriptor.workload_identity ? { for e in local.envs : e => "id-${var.app}-${e}-app" } : {}
}

data "azurerm_resources" "app_identity" {
  for_each = local.app_identity_names

  type                = "Microsoft.ManagedIdentity/userAssignedIdentities"
  resource_group_name = local.rg_apps
  name                = each.value
}

data "azurerm_user_assigned_identity" "app" {
  for_each = { for e, name in local.app_identity_names : e => name if length(data.azurerm_resources.app_identity[e].resources) > 0 }

  name                = each.value
  resource_group_name = local.rg_apps
}

data "azurerm_kubernetes_cluster" "tier" {
  count = length(local.app_identity_names) > 0 ? 1 : 0

  name                = "aks-platform-${var.tier}"
  resource_group_name = local.rg_aks
}

resource "azurerm_federated_identity_credential" "app" {
  for_each = data.azurerm_user_assigned_identity.app

  name                      = "aks-platform-${var.tier}-${module.descriptor.service_account}"
  user_assigned_identity_id = each.value.id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = data.azurerm_kubernetes_cluster.tier[0].oidc_issuer_url
  subject                   = "system:serviceaccount:${var.app}-${each.key}:${module.descriptor.service_account}"
}

resource "azurerm_key_vault_secret" "azure_client_id" {
  for_each = data.azurerm_user_assigned_identity.app

  name         = "azure-client-id"
  key_vault_id = azurerm_key_vault.app[each.key].id
  value        = each.value.client_id
  content_type = "client ID of id-${var.app}-${each.key}-app; ESO maps it to AZURE_CLIENT_ID"
}
