# Offline test of terraform/apps/tier with a mocked azurerm provider: nothing reaches Azure. random runs for real
# (local). The committed descriptor apps/sandbox.yaml is read from the same checkout.
# Run: terraform -chdir=terraform/apps/tier init -backend=false && terraform -chdir=terraform/apps/tier test

mock_provider "azurerm" {
  # The app identity exists (grants has run).
  mock_data "azurerm_resources" {
    defaults = {
      resources = [{
        id                  = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-shopcart-tdd-app"
        location            = "southcentralus"
        name                = "id-shopcart-tdd-app"
        resource_group_name = "rg-platform-nonprod-apps"
        tags                = {}
        type                = "Microsoft.ManagedIdentity/userAssignedIdentities"
      }]
    }
  }

  mock_data "azurerm_user_assigned_identity" {
    defaults = {
      id        = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-shopcart-tdd-app"
      client_id = "77777777-7777-7777-7777-777777777777"
    }
  }

  mock_data "azurerm_kubernetes_cluster" {
    defaults = {
      oidc_issuer_url = "https://southcentralus.oic.prod-aks.azure.com/11111111-1111-1111-1111-111111111111/dddddddd-dddd-dddd-dddd-dddddddddddd/"
    }
  }

  mock_data "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.OperationalInsights/workspaces/log-platform-nonprod"
    }
  }

  mock_data "azurerm_monitor_action_group" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-aks/providers/Microsoft.Insights/actionGroups/ag-platform-oncall"
    }
  }

  mock_data "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.Storage/storageAccounts/stbackupnonprod"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.KeyVault/vaults/kv-mock"
    }
  }

  mock_resource "azurerm_application_insights" {
    defaults = {
      id                = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.Insights/components/appi-mock"
      connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/"
    }
  }
}

# The same provider, but grants has not created the app identity yet.
mock_provider "azurerm" {
  alias = "identity_pending"

  mock_data "azurerm_resources" {
    defaults = {
      resources = []
    }
  }

  mock_data "azurerm_log_analytics_workspace" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.OperationalInsights/workspaces/log-platform-nonprod"
    }
  }

  mock_data "azurerm_monitor_action_group" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-aks/providers/Microsoft.Insights/actionGroups/ag-platform-oncall"
    }
  }

  mock_data "azurerm_storage_account" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-shared/providers/Microsoft.Storage/storageAccounts/stbackupnonprod"
    }
  }

  mock_resource "azurerm_key_vault" {
    defaults = {
      id = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.KeyVault/vaults/kv-mock"
    }
  }

  mock_resource "azurerm_application_insights" {
    defaults = {
      id                = "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/rg-platform-nonprod-apps/providers/Microsoft.Insights/components/appi-mock"
      connection_string = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://example.invalid/"
    }
  }
}

variables {
  tenant_id                   = "11111111-1111-1111-1111-111111111111"
  subscription_id             = "22222222-2222-2222-2222-222222222222"
  location                    = "southcentralus"
  backup_storage_account_name = "stbackupnonprod"
  app                         = "shopcart"
  tier                        = "nonprod"
  slo_alerts_enabled          = false
  descriptor = {
    schema   = 1
    name     = "shopcart"
    database = { engine = "mssql-2022-express" }
    secrets  = [{ name = "api-key", generate = true }, { name = "vendor-token" }]
    octopus  = { azureAccount = true, projects = [{ name = "shopcart" }] }
    azure = {
      resourceGroup    = true
      workloadIdentity = true
      serviceAccount   = "web"
      roles            = ["Storage Blob Data Contributor"]
    }
  }
}

run "nonprod_app_with_database_and_identity" {
  command = apply

  assert {
    condition     = output.environments == ["tdd", "uat"]
    error_message = "nonprod holds tdd and uat"
  }
  assert {
    condition     = output.vault_names == { tdd = "kv-shopcart-t-1bb8", uat = "kv-shopcart-u-630e" }
    error_message = "vault names follow kv-<app>-<e>-<hash4>"
  }
  assert {
    condition     = alltrue([for v in azurerm_key_vault.app : v.resource_group_name == "rg-platform-nonprod-apps" && v.rbac_authorization_enabled && v.purge_protection_enabled])
    error_message = "RBAC vaults with purge protection in rg-platform-<tier>-apps"
  }
  assert {
    condition     = toset([for s in azurerm_key_vault_secret.db : s.name]) == toset(["db-sa-password", "db-migrator-password", "db-app-password"]) && length(azurerm_key_vault_secret.db) == 6
    error_message = "three SQL login passwords per environment"
  }
  assert {
    condition     = alltrue([for s in azurerm_key_vault_secret.db : s.value_wo_version == 1 && s.value == null])
    error_message = "passwords are write-only, written once"
  }
  assert {
    condition     = azurerm_key_vault_secret.db["tdd/migrator"].content_type == "password; SQL login shopcart_migrator; rotated by rotate-db-passwords"
    error_message = "the migrator login is <app>_migrator"
  }
  assert {
    condition     = length(azurerm_key_vault_secret.app) == 4 && azurerm_key_vault_secret.app["uat/vendor-token"].content_type == "set by platform-operators; stand-in until then" && azurerm_key_vault_secret.app["tdd/api-key"].content_type == "generated once by terraform/apps/tier"
    error_message = "descriptor secrets: generated or stand-in"
  }
  assert {
    condition     = azurerm_managed_disk.db["tdd"].name == "disk-shopcart-tdd-db" && azurerm_managed_disk.db["tdd"].resource_group_name == "rg-platform-nonprod-data" && azurerm_managed_disk.db["tdd"].disk_size_gb == 8
    error_message = "disk-<app>-<env>-db of 8 GiB in the data group"
  }
  assert {
    condition     = azurerm_managed_disk.db["uat"].storage_account_type == "StandardSSD_LRS" && azurerm_managed_disk.db["uat"].zone == null && azurerm_managed_disk.db["uat"].create_option == "Empty"
    error_message = "Standard SSD, no zone"
  }
  assert {
    condition     = toset([for c in azurerm_storage_container.backup : c.name]) == toset(["shopcart-tdd", "shopcart-uat"])
    error_message = "one backup container per app-environment"
  }
  assert {
    condition     = azurerm_application_insights.app["tdd"].name == "appi-shopcart-tdd" && azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["uat"].name == "slo-fast-burn-shopcart-uat"
    error_message = "monitoring names follow §7.0"
  }
  assert {
    condition     = azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["tdd"].severity == 3 && !azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["tdd"].enabled
    error_message = "nonprod alerts: severity 3, disabled by the variable"
  }
  assert {
    condition     = azurerm_key_vault_secret.appinsights["tdd"].name == "appinsights-connection-string"
    error_message = "the connection string goes to the vault"
  }
  assert {
    condition     = azurerm_federated_identity_credential.app["tdd"].subject == "system:serviceaccount:shopcart-tdd:web" && azurerm_federated_identity_credential.app["uat"].subject == "system:serviceaccount:shopcart-uat:web"
    error_message = "workload identity subject system:serviceaccount:<app>-<env>:<serviceAccount>"
  }
  assert {
    condition     = azurerm_federated_identity_credential.app["tdd"].issuer == "https://southcentralus.oic.prod-aks.azure.com/11111111-1111-1111-1111-111111111111/dddddddd-dddd-dddd-dddd-dddddddddddd/"
    error_message = "the issuer is the tier cluster's"
  }
  assert {
    condition     = azurerm_key_vault_secret.azure_client_id["tdd"].name == "azure-client-id" && azurerm_key_vault_secret.azure_client_id["tdd"].value == "77777777-7777-7777-7777-777777777777"
    error_message = "the client ID reaches the vault (decision 11)"
  }
  assert {
    condition     = output.workload_identity == { tdd = "federated", uat = "federated" }
    error_message = "identity state"
  }
  assert {
    condition     = alltrue([for k in ["platform-tier", "platform-component", "platform-app", "platform-env"] : contains(keys(azurerm_managed_disk.db["tdd"].tags), k)]) && azurerm_key_vault.app["uat"].tags["platform-env"] == "uat"
    error_message = "per-app resources carry the four cost tags"
  }
}

run "nonprod_p3_enables_uat_alerts_only" {
  command = apply

  variables {
    slo_alerts_enabled     = true
    slo_alert_environments = ["uat"]
  }

  assert {
    condition     = azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["uat"].enabled && !azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["tdd"].enabled
    error_message = "P3: slo-fast-burn-<app>-uat live, tdd quiet"
  }
  assert {
    condition     = azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["uat"].severity == 3
    error_message = "nonprod alerts stay severity 3"
  }
}

run "slo_alert_environments_rejects_unknown_names" {
  command = plan

  variables {
    slo_alert_environments = ["staging"]
  }

  expect_failures = [var.slo_alert_environments]
}

run "prod_tier_holds_prod_only" {
  command = apply

  variables {
    tier               = "prod"
    slo_alerts_enabled = true
  }

  assert {
    condition     = output.vault_names == { prod = "kv-shopcart-p-a977" }
    error_message = "prod vault"
  }
  assert {
    condition     = azurerm_managed_disk.db["prod"].disk_size_gb == 32 && azurerm_managed_disk.db["prod"].resource_group_name == "rg-platform-prod-data"
    error_message = "32 GiB prod disk in rg-platform-prod-data"
  }
  assert {
    condition     = azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["prod"].severity == 1 && azurerm_monitor_scheduled_query_rules_alert_v2.slo_fast_burn["prod"].enabled
    error_message = "prod alerts page at severity 1"
  }
}

run "identity_pending_until_grants_runs" {
  command = apply

  providers = {
    azurerm = azurerm.identity_pending
  }

  assert {
    condition     = length(azurerm_federated_identity_credential.app) == 0 && length(azurerm_key_vault_secret.azure_client_id) == 0
    error_message = "without the identity, apps-apply skips the credential and the client ID"
  }
  assert {
    condition     = output.workload_identity == { tdd = "pending", uat = "pending" }
    error_message = "the identity is reported pending"
  }
  assert {
    condition     = length(azurerm_key_vault.app) == 2 && length(azurerm_managed_disk.db) == 2
    error_message = "everything else is still created"
  }
}

run "stateless_app_without_database" {
  command = apply

  variables {
    descriptor = {
      schema       = 1
      name         = "shopcart"
      environments = ["tdd"]
      octopus      = { projects = [{ name = "shopcart" }] }
    }
  }

  assert {
    condition     = length(azurerm_key_vault.app) == 1 && length(azurerm_key_vault_secret.db) == 0 && length(azurerm_managed_disk.db) == 0 && length(azurerm_storage_container.backup) == 0
    error_message = "a stateless app gets its vault and nothing for a database"
  }
  assert {
    condition     = length(azurerm_federated_identity_credential.app) == 0 && output.workload_identity == { tdd = "none" }
    error_message = "no workload identity unless declared"
  }
}

run "committed_sandbox" {
  command = apply

  variables {
    app        = "sandbox"
    descriptor = null
  }

  assert {
    condition     = output.vault_names == { tdd = "kv-sandbox-t-cbe6", uat = "kv-sandbox-u-ced1" }
    error_message = "the sandbox vaults"
  }
  assert {
    condition     = output.database_disk_ids["tdd"] != null && toset(keys(output.backup_containers)) == toset(["tdd", "uat"])
    error_message = "the sandbox database storage"
  }
}

