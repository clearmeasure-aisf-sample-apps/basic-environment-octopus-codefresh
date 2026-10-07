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

  # Every grant of the interim mode, as key=role, sorted. A grant that is added, removed or renamed fails here until
  # this list changes with it, so the list is reviewed with the grant (who holds which role is the owner's decision).
  # The first line of the failure's diff that differs names the grant.
  assert {
    condition = sort([for k, g in local.grants : "${k}=${g.role}"]) == tolist([
      "conformance-acrpull=AcrPull",
      "conformance-aks-cluster-admin-build=Azure Kubernetes Service RBAC Cluster Admin",
      "conformance-aks-cluster-admin-nonprod=Azure Kubernetes Service RBAC Cluster Admin",
      "conformance-aks-cluster-admin-prod=Azure Kubernetes Service RBAC Cluster Admin",
      "conformance-budget-reader=Reader",
      "conformance-reader-rg-platform-build=Reader",
      "conformance-reader-rg-platform-global=Reader",
      "conformance-reader-rg-platform-nonprod-aks=Reader",
      "conformance-reader-rg-platform-nonprod-apps=Reader",
      "conformance-reader-rg-platform-nonprod-data=Reader",
      "conformance-reader-rg-platform-nonprod-shared=Reader",
      "conformance-reader-rg-platform-prod-aks=Reader",
      "conformance-reader-rg-platform-prod-apps=Reader",
      "conformance-reader-rg-platform-prod-data=Reader",
      "conformance-reader-rg-platform-prod-shared=Reader",
      "controlplane-nonprod-contributor-data=Contributor",
      "controlplane-nonprod-mi-operator-kubelet=Managed Identity Operator",
      "controlplane-nonprod-network-contributor-shared=Network Contributor",
      "controlplane-prod-contributor-data=Contributor",
      "controlplane-prod-mi-operator-kubelet=Managed Identity Operator",
      "controlplane-prod-network-contributor-shared=Network Contributor",
      "dashboard-status-reader-nonprod=Reader",
      "dashboard-status-reader-prod=Reader",
      "db-backup-nonprod-blob-contributor=Storage Blob Data Contributor",
      "db-backup-prod-blob-contributor=Storage Blob Data Contributor",
      "eso-nonprod-kv-secrets-user-aks=Key Vault Secrets User",
      "eso-nonprod-kv-secrets-user-apps=Key Vault Secrets User",
      "eso-prod-kv-secrets-user-aks=Key Vault Secrets User",
      "eso-prod-kv-secrets-user-apps=Key Vault Secrets User",
      "kubelet-nonprod-acrpull=AcrPull",
      "kubelet-prod-acrpull=AcrPull",
      "kyverno-nonprod-acrpull=AcrPull",
      "kyverno-prod-acrpull=AcrPull",
      "lifecycle-nonprod-aks-cluster-admin=Azure Kubernetes Service RBAC Cluster Admin",
      "lifecycle-nonprod-contributor-aks=Contributor",
      "lifecycle-nonprod-contributor-apps=Contributor",
      "lifecycle-nonprod-contributor-data=Contributor",
      "lifecycle-nonprod-contributor-shared=Contributor",
      "lifecycle-nonprod-kv-secrets-officer-aks=Key Vault Secrets Officer",
      "lifecycle-nonprod-kv-secrets-officer-apps=Key Vault Secrets Officer",
      "lifecycle-nonprod-state-blob-contributor=Storage Blob Data Contributor",
      "lifecycle-prod-aks-cluster-admin=Azure Kubernetes Service RBAC Cluster Admin",
      "lifecycle-prod-contributor-aks=Contributor",
      "lifecycle-prod-contributor-apps=Contributor",
      "lifecycle-prod-contributor-data=Contributor",
      "lifecycle-prod-contributor-shared=Contributor",
      "lifecycle-prod-kv-secrets-officer-aks=Key Vault Secrets Officer",
      "lifecycle-prod-kv-secrets-officer-apps=Key Vault Secrets Officer",
      "lifecycle-prod-state-blob-contributor=Storage Blob Data Contributor",
      "octopus-acr-pull-acrpull=AcrPull",
      "platform-operators-aks-cluster-admin-build=Azure Kubernetes Service RBAC Cluster Admin",
      "platform-operators-aks-cluster-admin-nonprod=Azure Kubernetes Service RBAC Cluster Admin",
      "platform-operators-aks-cluster-admin-prod=Azure Kubernetes Service RBAC Cluster Admin",
      "platform-operators-kv-secrets-officer-nonprod-aks=Key Vault Secrets Officer",
      "platform-operators-kv-secrets-officer-nonprod-apps=Key Vault Secrets Officer",
      "platform-operators-kv-secrets-officer-prod-aks=Key Vault Secrets Officer",
      "platform-operators-kv-secrets-officer-prod-apps=Key Vault Secrets Officer",
      "provisioner-build-aks-cluster-admin=Azure Kubernetes Service RBAC Cluster Admin",
      "provisioner-nonprod-aks-cluster-admin=Azure Kubernetes Service RBAC Cluster Admin",
      "provisioner-prod-aks-cluster-admin=Azure Kubernetes Service RBAC Cluster Admin",
      "provisioner-state-global-blob-contributor=Storage Blob Data Contributor",
    ])
    error_message = "The interim grant set differs from the list in this test: change role-assignments.tf and the list together."
  }

  assert {
    condition     = sort(keys(azurerm_role_assignment.this)) == sort(keys(local.grants))
    error_message = "Every grant of local.grants must become one role assignment."
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

  # The conformance principal's grants in least-privilege mode, as key=role: the three AKS roles of P1-03 and the
  # node-group reads replace the interim cluster admin.
  assert {
    condition = sort([for k, g in local.grants : "${k}=${g.role}" if startswith(k, "conformance-")]) == tolist([
      "conformance-acrpull=AcrPull",
      "conformance-aks-cluster-user-build=Azure Kubernetes Service Cluster User Role",
      "conformance-aks-cluster-user-nonprod=Azure Kubernetes Service Cluster User Role",
      "conformance-aks-cluster-user-prod=Azure Kubernetes Service Cluster User Role",
      "conformance-aks-rbac-reader-build=Azure Kubernetes Service RBAC Reader",
      "conformance-aks-rbac-reader-nonprod=Azure Kubernetes Service RBAC Reader",
      "conformance-aks-rbac-reader-prod=Azure Kubernetes Service RBAC Reader",
      "conformance-aks-rbac-writer-sandbox-prod=Azure Kubernetes Service RBAC Writer",
      "conformance-aks-rbac-writer-sandbox-tdd=Azure Kubernetes Service RBAC Writer",
      "conformance-aks-rbac-writer-sandbox-uat=Azure Kubernetes Service RBAC Writer",
      "conformance-budget-reader=Reader",
      "conformance-reader-rg-platform-build-aks-nodes=Reader",
      "conformance-reader-rg-platform-build=Reader",
      "conformance-reader-rg-platform-global=Reader",
      "conformance-reader-rg-platform-nonprod-aks-nodes=Reader",
      "conformance-reader-rg-platform-nonprod-aks=Reader",
      "conformance-reader-rg-platform-nonprod-apps=Reader",
      "conformance-reader-rg-platform-nonprod-data=Reader",
      "conformance-reader-rg-platform-nonprod-shared=Reader",
      "conformance-reader-rg-platform-prod-aks-nodes=Reader",
      "conformance-reader-rg-platform-prod-aks=Reader",
      "conformance-reader-rg-platform-prod-apps=Reader",
      "conformance-reader-rg-platform-prod-data=Reader",
      "conformance-reader-rg-platform-prod-shared=Reader",
    ])
    error_message = "The conformance principal's least-privilege grants differ from the list in this test: change role-assignments.tf and the list together."
  }

  assert {
    condition     = sort([for k in keys(local.grants) : k if !startswith(k, "conformance-")]) == sort([for k in keys(run.defaults_stay_within_todays_roles.role_assignments) : k if !startswith(k, "conformance-")])
    error_message = "Least-privilege mode must change the conformance principal's grants only: every other grant equals the interim mode's."
  }

  assert {
    condition     = sort(keys(azurerm_role_assignment.this)) == sort(keys(local.grants))
    error_message = "Every grant of local.grants must become one role assignment."
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
