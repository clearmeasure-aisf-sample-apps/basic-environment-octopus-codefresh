# Offline apply of terraform/foundation against mocked providers: every resource, grant and output
# evaluates with concrete IDs (no Azure or Graph call).
#   cd terraform/foundation && terraform init -backend=false && terraform test

mock_provider "azurerm" {
  mock_data "azurerm_subscription" {
    defaults = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000001"
      subscription_id = "00000000-0000-0000-0000-000000000001"
      quota_id        = "PayAsYouGo_2014-09-01"
    }
  }
  mock_data "azurerm_client_config" {
    defaults = { object_id = "00000000-0000-0000-0000-00000000000a" }
  }
  mock_data "azurerm_role_definition" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.Authorization/roleDefinitions/00000000-0000-0000-0000-0000000000ff" }
  }
  mock_resource "azurerm_resource_group" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-mock" }
  }
  mock_resource "azurerm_container_registry" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-build/providers/Microsoft.ContainerRegistry/registries/acrplatformt3st01", login_server = "acrplatformt3st01.azurecr.io" }
  }
  mock_resource "azurerm_container_registry_scope_map" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-build/providers/Microsoft.ContainerRegistry/registries/acrplatformt3st01/scopeMaps/x-scope" }
  }
  mock_resource "azurerm_user_assigned_identity" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-mock/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-mock", principal_id = "00000000-0000-0000-0000-0000000000c1", client_id = "00000000-0000-0000-0000-0000000000c2" }
  }
  mock_resource "azurerm_storage_account" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-mock/providers/Microsoft.Storage/storageAccounts/stmock", primary_blob_endpoint = "https://stmock.blob.core.windows.net/" }
  }
  mock_resource "azurerm_storage_container" {
    defaults = { id = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-mock/providers/Microsoft.Storage/storageAccounts/stmock/blobServices/default/containers/tfstate" }
  }
}
mock_provider "azuread" {
  mock_data "azuread_client_config" {
    defaults = { object_id = "00000000-0000-0000-0000-00000000000a" }
  }
  mock_resource "azuread_application" {
    defaults = { client_id = "00000000-0000-0000-0000-0000000000d1", object_id = "00000000-0000-0000-0000-0000000000d2", id = "/applications/00000000-0000-0000-0000-0000000000d2" }
  }
  mock_resource "azuread_service_principal" {
    defaults = { object_id = "00000000-0000-0000-0000-0000000000d3", id = "/servicePrincipals/00000000-0000-0000-0000-0000000000d3" }
  }
  mock_resource "azuread_group" {
    defaults = { object_id = "00000000-0000-0000-0000-0000000000d4", id = "/groups/00000000-0000-0000-0000-0000000000d4" }
  }
}
variables {
  tenant_id                          = "00000000-0000-0000-0000-000000000002"
  subscription_id                    = "00000000-0000-0000-0000-000000000001"
  location                           = "southcentralus"
  name_suffix                        = "t3st01"
  octopus_url                        = "https://example.octopus.app"
  octopus_space_slug                 = "platform-space"
  platform_operators_group_object_id = "00000000-0000-0000-0000-00000000000b"
}
run "outputs_resolve_in_the_interim_mode" {
  command = apply
  assert {
    condition     = output.budgets.status == "created" && output.conformance_settings.Tiers.build.ResourceGroups == tolist(["rg-platform-build", "rg-platform-build-aks-nodes", "rg-platform-global"])
    error_message = "Created budgets and the harness tier groups expected."
  }
  assert {
    condition     = sort(keys(output.role_assignments)) == sort(keys(local.grants)) && alltrue([for g in values(output.role_assignments) : startswith(g.scope, "/subscriptions/") && g.principal != "" && g.role != ""])
    error_message = "Output role_assignments must hold every grant of local.grants with a resolved scope, principal and role."
  }
  assert {
    condition     = contains(keys(output.role_assignments), "conformance-aks-cluster-admin-build") && !contains(keys(output.role_assignments), "conformance-aks-rbac-reader-build")
    error_message = "The interim mode grants the conformance principal cluster admin, and none of the least-privilege roles."
  }
}
run "outputs_resolve_in_least_privilege_mode" {
  command = apply
  variables {
    conformance_least_privilege = true
  }
  assert {
    condition     = sort(keys(output.role_assignments)) == sort(keys(local.grants)) && alltrue([for g in values(output.role_assignments) : startswith(g.scope, "/subscriptions/") && g.principal != "" && g.role != ""])
    error_message = "Output role_assignments must hold every grant of local.grants with a resolved scope, principal and role."
  }
  assert {
    condition     = !contains(keys(output.role_assignments), "conformance-aks-cluster-admin-build") && output.role_assignments["conformance-aks-rbac-reader-build"].scope == "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-build/providers/Microsoft.ContainerService/managedClusters/aks-platform-build" && output.role_assignments["conformance-reader-rg-platform-build-aks-nodes"].role == "Reader"
    error_message = "Least-privilege mode replaces the interim cluster admin with the RBAC Reader on the build cluster and the node-group Reader."
  }
}
