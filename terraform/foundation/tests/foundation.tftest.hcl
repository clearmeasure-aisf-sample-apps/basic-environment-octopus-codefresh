# Offline plan tests of terraform/foundation with mocked providers: no Azure or Graph call.
#   cd terraform/foundation && terraform init -backend=false && terraform test
# They prove the grant set stays inside the provisioner's assignable roles, the least-privilege switch,
# the budget decision per subscription offer, the names and the cost tags.

mock_provider "azurerm" {
  mock_data "azurerm_subscription" {
    defaults = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000001"
      subscription_id = "00000000-0000-0000-0000-000000000001"
      quota_id        = "PayAsYouGo_2014-09-01"
    }
  }

  mock_data "azurerm_client_config" {
    defaults = {
      object_id = "00000000-0000-0000-0000-00000000000a"
    }
  }

  mock_data "azurerm_role_definition" {
    defaults = {
      id = "/subscriptions/00000000-0000-0000-0000-000000000001/providers/Microsoft.Authorization/roleDefinitions/00000000-0000-0000-0000-0000000000ff"
    }
  }
}

mock_provider "azuread" {
  mock_data "azuread_client_config" {
    defaults = {
      object_id = "00000000-0000-0000-0000-00000000000a"
    }
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

run "defaults_stay_within_todays_roles" {
  command = plan

  assert {
    condition     = length(azurerm_resource_group.this) == 10
    error_message = "Expected the ten platform resource groups."
  }

  assert {
    condition = alltrue([
      for name in keys(azurerm_resource_group.this) : contains([
        "rg-platform-global", "rg-platform-build",
        "rg-platform-nonprod-shared", "rg-platform-nonprod-aks", "rg-platform-nonprod-data", "rg-platform-nonprod-apps",
        "rg-platform-prod-shared", "rg-platform-prod-aks", "rg-platform-prod-data", "rg-platform-prod-apps",
      ], name)
    ])
    error_message = "Unexpected resource group name."
  }

  assert {
    condition     = length(azurerm_role_assignment.this) == 58
    error_message = "Expected 56 role assignments in the interim mode."
  }

  assert {
    condition     = alltrue([for g in values(local.grants) : contains(local.roles_assignable_today, g.role)])
    error_message = "A grant uses a role outside the Owner script's current list."
  }

  assert {
    condition     = alltrue([for g in values(local.grants) : contains(["ServicePrincipal", "Group"], g.type)])
    error_message = "Every grant must name a ServicePrincipal or Group principal type (ABAC condition)."
  }

  assert {
    condition     = length([for k, g in local.grants : k if g.role == "Azure Kubernetes Service RBAC Cluster Admin" && startswith(k, "conformance-")]) == 3
    error_message = "The conformance principal needs the interim cluster admin on the three cluster groups."
  }

  assert {
    condition     = length(azurerm_consumption_budget_subscription.platform) == 3
    error_message = "Budgets must exist on a supported offer."
  }

  assert {
    condition     = azurerm_container_registry.this.name == "acrplatformt3st01" && azurerm_container_registry.this.sku == "Standard" && !azurerm_container_registry.this.admin_enabled && !azurerm_container_registry.this.anonymous_pull_enabled
    error_message = "Registry must be Standard, without admin user or anonymous pull."
  }

  assert {
    condition     = length(azurerm_container_registry_token.this) == 5 && length(azurerm_container_registry_scope_map.this) == 5
    error_message = "Expected five tokens with five scope maps."
  }

  assert {
    condition     = azurerm_container_registry_scope_map.this["cf-platform-retention"].actions == tolist(["repositories/apps/*/content/read", "repositories/apps/*/content/delete", "repositories/apps/*/metadata/read", "repositories/apps/*/metadata/write", "repositories/apps-previews/*/content/read", "repositories/apps-previews/*/content/delete", "repositories/apps-previews/*/metadata/read", "repositories/apps-previews/*/metadata/write"])
    error_message = "Retention token scope differs from §7.0."
  }

  assert {
    condition     = alltrue([for a in values(azurerm_storage_account.tfstate) : !a.shared_access_key_enabled]) && alltrue([for a in values(azurerm_storage_account.backup) : !a.shared_access_key_enabled])
    error_message = "State and backup accounts must disable shared keys."
  }

  assert {
    condition     = azurerm_storage_account.tfstate["global"].name == "sttfglobalt3st01" && azurerm_storage_account.backup["prod"].name == "stbkpprodt3st01"
    error_message = "Storage names must follow the suffix pattern."
  }

  assert {
    condition     = azurerm_federated_identity_credential.octopus["id-platform-lifecycle-nonprod"].subject == "space:platform-space:project:platform-infrastructure:environment:infra-nonprod" && azurerm_federated_identity_credential.octopus["id-octopus-acr-pull"].subject == "space:platform-space:feed:acr-apps"
    error_message = "Octopus subjects differ from §7.0."
  }

  assert {
    condition     = alltrue([for f in values(azurerm_federated_identity_credential.octopus) : f.issuer == "https://example.octopus.app" && f.audience == tolist(["api://AzureADTokenExchange"])])
    error_message = "Octopus issuer or audience is wrong."
  }

  assert {
    condition = alltrue(concat(
      [for r in values(azurerm_resource_group.this) : contains(keys(r.tags), "platform-tier") && contains(keys(r.tags), "platform-component")],
      [for r in values(azurerm_user_assigned_identity.tier) : contains(keys(r.tags), "platform-tier") && contains(keys(r.tags), "platform-component")],
      [for r in values(azurerm_storage_account.tfstate) : contains(keys(r.tags), "platform-tier") && contains(keys(r.tags), "platform-component")],
      [for r in values(azurerm_storage_account.backup) : contains(keys(r.tags), "platform-tier") && contains(keys(r.tags), "platform-component")],
      [contains(keys(azurerm_container_registry.this.tags), "platform-tier"), contains(keys(azurerm_user_assigned_identity.octopus_acr_pull.tags), "platform-component")],
    ))
    error_message = "Every tagged resource needs platform-tier and platform-component."
  }


  assert {
    condition     = length(azuread_application.conformance.password) == 0
    error_message = "sp-platform-conformance must not get a password in state."
  }
}

run "least_privilege_replaces_the_interim_admin" {
  command = plan

  variables {
    conformance_least_privilege = true
  }

  assert {
    condition     = length(azurerm_role_assignment.this) == 67
    error_message = "Expected 65 role assignments in least-privilege mode."
  }

  assert {
    condition     = length([for k, g in local.grants : k if g.role == "Azure Kubernetes Service RBAC Cluster Admin" && startswith(k, "conformance-")]) == 0
    error_message = "The interim cluster admin must be gone."
  }

  assert {
    condition     = local.grants["conformance-aks-rbac-writer-sandbox-prod"].scope == "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-prod-aks/providers/Microsoft.ContainerService/managedClusters/aks-platform-prod/namespaces/sandbox-prod"
    error_message = "Writer scope must be the sandbox-prod namespace of aks-platform-prod."
  }

  assert {
    condition     = local.grants["conformance-reader-rg-platform-build-aks-nodes"].scope == "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-platform-build-aks-nodes"
    error_message = "Node-group Reader must target rg-platform-build-aks-nodes."
  }

  assert {
    condition     = alltrue([for g in values(local.grants) : contains(local.roles_assignable_after_p103, g.role)])
    error_message = "A grant uses a role outside the changed Owner script's list."
  }
}

run "sponsored_offer_skips_budgets" {
  command = plan

  override_data {
    target = data.azurerm_subscription.current
    values = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000001"
      subscription_id = "00000000-0000-0000-0000-000000000001"
      quota_id        = "Sponsored_2016-01-01"
    }
  }

  assert {
    condition     = length(azurerm_consumption_budget_subscription.platform) == 0
    error_message = "Budgets must be skipped on a Sponsorship offer by default."
  }

  assert {
    condition     = startswith(output.budgets.status, "skipped: Cost Management does not support quota ID Sponsored_2016-01-01")
    error_message = "The budget status must say why budgets were skipped."
  }
}

run "budgets_can_be_forced" {
  command = plan

  variables {
    budgets_enabled = true
  }

  override_data {
    target = data.azurerm_subscription.current
    values = {
      id              = "/subscriptions/00000000-0000-0000-0000-000000000001"
      subscription_id = "00000000-0000-0000-0000-000000000001"
      quota_id        = "Sponsored_2016-01-01"
    }
  }

  assert {
    condition     = length(azurerm_consumption_budget_subscription.platform) == 3
    error_message = "budgets_enabled = true must create the budgets."
  }

  assert {
    condition     = sort(tolist(azurerm_consumption_budget_subscription.platform["build"].filter[0].dimension)[0].values) == tolist(["rg-platform-build", "rg-platform-build-aks-nodes", "rg-platform-global"])
    error_message = "The build budget must filter the global, build and build node groups."
  }
}

run "rejects_a_bad_suffix" {
  command = plan

  variables {
    name_suffix = "Bad-Suffix"
  }

  expect_failures = [var.name_suffix]
}

run "rejects_an_issuer_with_trailing_slash" {
  command = plan

  variables {
    octopus_url = "https://example.octopus.app/"
  }

  expect_failures = [var.octopus_url]
}

run "dashboard_status_reads_the_two_cluster_groups" {
  command = plan

  assert {
    condition     = azurerm_federated_identity_credential.dashboard_status.issuer == "https://token.actions.githubusercontent.com" && azurerm_federated_identity_credential.dashboard_status.subject == "repo:clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh-dashboard:ref:refs/heads/main"
    error_message = "id-dashboard-status must be federated to the main branch of the dashboard repository through the GitHub issuer."
  }

  assert {
    condition     = sort([for k, g in local.grants : "${k}=${g.role}" if startswith(k, "dashboard-status-")]) == tolist(["dashboard-status-reader-nonprod=Reader", "dashboard-status-reader-prod=Reader"])
    error_message = "id-dashboard-status must hold Reader, once per tier (on the tier's cluster group), and nothing else."
  }

  assert {
    condition     = azurerm_user_assigned_identity.dashboard_status.resource_group_name == "rg-platform-build"
    error_message = "id-dashboard-status belongs to rg-platform-build, where no tier identity can add a credential to it."
  }
}

run "rejects_a_bad_dashboard_repository" {
  command = plan

  variables {
    dashboard_repository = "not a repository"
  }

  expect_failures = [var.dashboard_repository]
}
