# Terraform state and database backups (§7.0 "Storage accounts", ADR-IR34 decision 12).
#
#   <tfstate-storage-account-global>   rg-platform-global         container tfstate: foundation.tfstate,
#                                                                 build.tfstate, app-grants-<app>.tfstate,
#                                                                 octopus-space.tfstate
#   <tfstate-storage-account-<tier>>   rg-platform-<tier>-shared  container tfstate: tier-<tier>.tfstate,
#                                                                 apps-<app>.tfstate
#   <backup-storage-account-<tier>>    rg-platform-<tier>-shared  containers <app>-<env>, created by
#                                                                 terraform/apps/tier
#
# State holds values the providers cannot keep out, and backups hold app data, so every account:
#   - disables shared keys and local users; readers and writers authenticate with Entra ID and need
#     Storage Blob Data Contributor (role-assignments.tf); the backup Jobs use user-delegation SAS;
#   - keeps soft delete (state also keeps blob versions) to recover a corrupted or deleted blob;
#   - stays reachable from the internet: Octopus dynamic workers, Codefresh and operator sessions have
#     no fixed egress yet (Q7), so Entra RBAC is the control.
# prevent_destroy protects all five: losing state orphans every resource, and losing backups loses data.

locals {
  state_accounts = {
    global  = { name = local.tfstate_account_names.global, resource_group = local.rg_global }
    nonprod = { name = local.tfstate_account_names.nonprod, resource_group = local.rg_tier["nonprod"].shared }
    prod    = { name = local.tfstate_account_names.prod, resource_group = local.rg_tier["prod"].shared }
  }
}

resource "azurerm_storage_account" "tfstate" {
  for_each = local.state_accounts

  name                = each.value.name
  resource_group_name = azurerm_resource_group.this[each.value.resource_group].name
  location            = var.location

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = "ZRS"

  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  local_user_enabled                = false
  allow_nested_items_to_be_public   = false
  cross_tenant_replication_enabled  = false
  https_traffic_only_enabled        = true
  min_tls_version                   = "TLS1_2"
  infrastructure_encryption_enabled = true
  public_network_access             = "Enabled"

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = 30
    }

    container_delete_retention_policy {
      days = 30
    }
  }

  tags = merge(local.base_tags, {
    "platform-tier"      = each.key
    "platform-component" = "terraform-state"
  })

  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_storage_container" "tfstate" {
  for_each = local.state_accounts

  name                  = "tfstate"
  storage_account_id    = azurerm_storage_account.tfstate[each.key].id
  container_access_type = "private"
}

resource "azurerm_storage_account" "backup" {
  for_each = toset(local.tiers)

  name                = local.backup_account_names[each.key]
  resource_group_name = azurerm_resource_group.this[local.rg_tier[each.key].shared].name
  location            = var.location

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = "ZRS"

  shared_access_key_enabled         = false
  default_to_oauth_authentication   = true
  local_user_enabled                = false
  allow_nested_items_to_be_public   = false
  cross_tenant_replication_enabled  = false
  https_traffic_only_enabled        = true
  min_tls_version                   = "TLS1_2"
  infrastructure_encryption_enabled = true
  public_network_access             = "Enabled"

  blob_properties {
    delete_retention_policy {
      days = 14
    }

    container_delete_retention_policy {
      days = 14
    }
  }

  tags = merge(local.base_tags, {
    "platform-tier"      = each.key
    "platform-component" = "db-backup"
  })

  lifecycle {
    prevent_destroy = true
  }
}

# Backups older than backup_retention_days are deleted; restores use the latest backup (ADR-IR34
# decision 8, CAP-AZ-009, CAP-AZ-010).
resource "azurerm_storage_management_policy" "backup" {
  for_each = toset(local.tiers)

  storage_account_id = azurerm_storage_account.backup[each.key].id

  rule {
    name    = "expire-backups"
    enabled = true

    filters {
      blob_types = ["blockBlob"]
    }

    actions {
      base_blob {
        delete_after_days_since_creation_greater_than = var.backup_retention_days
      }
    }
  }
}
