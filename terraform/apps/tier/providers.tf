provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id

  # terraform/foundation registers the resource providers; the lifecycle identity cannot.
  resource_provider_registrations = "none"

  # The state account, the backup account and the vaults are reached with Entra ID only.
  storage_use_azuread = true

  features {
    key_vault {
      # Purge protection is on. A vault deleted with its app stays soft-deleted for the retention period, and a
      # re-onboarded app with the same slug recovers it (the name is deterministic).
      purge_soft_delete_on_destroy          = false
      recover_soft_deleted_key_vaults       = true
      purge_soft_deleted_secrets_on_destroy = false
      recover_soft_deleted_secrets          = true
    }
  }
}
