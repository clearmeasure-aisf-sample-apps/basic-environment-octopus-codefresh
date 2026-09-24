# Offline test of terraform/apps/grants with a mocked azurerm provider: nothing reaches Azure or Entra.
# Run: terraform -chdir=terraform/apps/grants init -backend=false && terraform -chdir=terraform/apps/grants test

mock_provider "azurerm" {
  mock_resource "azurerm_user_assigned_identity" {
    defaults = {
      id           = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-mock"
      client_id    = "88888888-8888-8888-8888-888888888888"
      principal_id = "99999999-9999-9999-9999-999999999999"
    }
  }

  mock_resource "azurerm_resource_group" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-app-mock"
    }
  }
}

variables {
  tenant_id          = "11111111-1111-1111-1111-111111111111"
  subscription_id    = "22222222-2222-2222-2222-222222222222"
  location           = "southcentralus"
  octopus_url        = "https://example.octopus.app"
  octopus_space_slug = "test-space"
}

run "deploy_identities_for_an_octopus_account" {
  command = apply

  variables {
    app = "shopcart"
    descriptor = {
      schema  = 1
      name    = "shopcart"
      octopus = { azureAccount = true, projects = [{ name = "shopcart" }, { name = "shopcart-jobs" }] }
    }
  }

  assert {
    condition     = toset([for i in azurerm_user_assigned_identity.deploy : i.name]) == toset(["id-shopcart-tdd-deploy", "id-shopcart-uat-deploy", "id-shopcart-prod-deploy"])
    error_message = "one deploy identity per environment"
  }
  assert {
    condition     = azurerm_user_assigned_identity.deploy["prod"].resource_group_name == "rg-platform-prod-apps" && azurerm_user_assigned_identity.deploy["uat"].resource_group_name == "rg-platform-nonprod-apps"
    error_message = "identities live in their tier's apps group"
  }
  assert {
    condition     = length(azurerm_federated_identity_credential.deploy) == 6 && azurerm_federated_identity_credential.deploy["uat/shopcart-jobs"].subject == "space:test-space:project:shopcart-jobs:environment:uat"
    error_message = "one Octopus credential per project and environment"
  }
  assert {
    condition     = alltrue([for c in azurerm_federated_identity_credential.deploy : c.issuer == "https://example.octopus.app"])
    error_message = "the issuer is the Octopus URL without a trailing slash"
  }
  assert {
    condition     = azurerm_role_assignment.deploy_vault["tdd"].scope == "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.KeyVault/vaults/kv-shopcart-t-1bb8"
    error_message = "the deploy identity reads its own tdd vault"
  }
  assert {
    condition     = azurerm_role_assignment.deploy_vault["prod"].scope == "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-prod-apps/providers/Microsoft.KeyVault/vaults/kv-shopcart-p-a977" && azurerm_role_assignment.deploy_vault["prod"].role_definition_name == "Key Vault Secrets User"
    error_message = "the deploy identity reads its own prod vault, read-only"
  }
  assert {
    condition     = alltrue([for a in azurerm_role_assignment.deploy_vault : a.principal_type == "ServicePrincipal"])
    error_message = "every assignment names a service principal (ABAC condition)"
  }
  assert {
    condition     = length(azurerm_resource_group.app) == 0 && length(azurerm_role_assignment.deploy_group) == 0 && length(azurerm_user_assigned_identity.app) == 0
    error_message = "no app group and no app identity unless declared"
  }
  assert {
    condition     = output.deploy_identities["tdd"].octopus_account == "azure-shopcart-tdd"
    error_message = "the Octopus account name"
  }
}

run "app_group_workload_identity_and_roles" {
  command = apply

  variables {
    app                             = "shopcart"
    conformance_principal_object_id = "44444444-4444-4444-4444-444444444444"
    descriptor = {
      schema       = 1
      name         = "shopcart"
      environments = ["tdd", "uat"]
      octopus      = { azureAccount = true, projects = [{ name = "shopcart" }] }
      azure = {
        resourceGroup    = true
        workloadIdentity = true
        serviceAccount   = "web"
        roles            = ["Storage Blob Data Contributor", "Reader"]
      }
    }
  }

  assert {
    condition     = keys(azurerm_resource_group.app) == ["nonprod"] && azurerm_resource_group.app["nonprod"].name == "rg-app-shopcart-nonprod"
    error_message = "a group only for the tiers the app runs in"
  }
  assert {
    condition     = azurerm_resource_group.app["nonprod"].tags["platform-env"] == "tdd+uat" && azurerm_resource_group.app["nonprod"].tags["platform-app"] == "shopcart"
    error_message = "the app group carries the cost tags"
  }
  assert {
    condition     = toset([for i in azurerm_user_assigned_identity.app : i.name]) == toset(["id-shopcart-tdd-app", "id-shopcart-uat-app"])
    error_message = "one app identity per environment"
  }
  assert {
    condition     = length(azurerm_role_assignment.app_roles) == 4 && toset([for a in azurerm_role_assignment.app_roles : a.role_definition_name]) == toset(["Storage Blob Data Contributor", "Reader"])
    error_message = "descriptor roles per app identity"
  }
  assert {
    condition     = alltrue([for a in azurerm_role_assignment.app_roles : a.scope == azurerm_resource_group.app["nonprod"].id])
    error_message = "app roles apply on the app's own group only"
  }
  assert {
    condition     = length(azurerm_role_assignment.deploy_group) == 2 && alltrue([for a in azurerm_role_assignment.deploy_group : a.role_definition_name == "Contributor"])
    error_message = "the deploy identities manage the app group"
  }
  assert {
    condition     = length(azurerm_role_assignment.conformance_reader) == 1 && azurerm_role_assignment.conformance_reader["nonprod"].role_definition_name == "Reader"
    error_message = "the conformance principal reads the app group"
  }
  assert {
    condition     = length(azurerm_role_assignment.conformance_sandbox_vault) == 0
    error_message = "the vault grant is for the sandbox only"
  }
}

run "sandbox_conformance_vault_grant" {
  command = apply

  variables {
    app                             = "sandbox"
    conformance_principal_object_id = "44444444-4444-4444-4444-444444444444"
  }

  assert {
    condition     = azurerm_role_assignment.conformance_sandbox_vault["tdd"].scope == "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.KeyVault/vaults/kv-sandbox-t-cbe6"
    error_message = "the conformance principal writes canaries only to the sandbox tdd vault"
  }
  assert {
    condition     = azurerm_role_assignment.conformance_sandbox_vault["tdd"].role_definition_name == "Key Vault Secrets Officer" && azurerm_role_assignment.conformance_sandbox_vault["tdd"].principal_id == "44444444-4444-4444-4444-444444444444"
    error_message = "Key Vault Secrets Officer for the conformance principal"
  }
  assert {
    condition     = length(azurerm_user_assigned_identity.deploy) == 0 && length(azurerm_resource_group.app) == 0
    error_message = "the committed sandbox descriptor asks for no other Azure access"
  }
}

run "no_azure_access_creates_nothing" {
  command = apply

  variables {
    app        = "shopcart"
    descriptor = { schema = 1, name = "shopcart", octopus = { projects = [{ name = "shopcart" }] } }
  }

  assert {
    condition     = length(azurerm_user_assigned_identity.deploy) + length(azurerm_user_assigned_identity.app) + length(azurerm_resource_group.app) + length(azurerm_federated_identity_credential.deploy) == 0
    error_message = "an app without Azure access gets nothing from the provisioner"
  }
}

run "rejects_a_trailing_slash_issuer" {
  command = plan

  variables {
    app         = "sandbox"
    octopus_url = "https://example.octopus.app/"
  }

  expect_failures = [var.octopus_url]
}
