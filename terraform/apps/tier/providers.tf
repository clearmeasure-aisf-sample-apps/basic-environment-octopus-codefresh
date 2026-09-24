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

    application_insights {
      # Azure adds a rule "Failure Anomalies - <component>" to every new component and points it at one action group per
      # subscription, "Application Insights Smart Detection", which mails the ARM monitoring roles: prod rules then
      # notify through a group in the nonprod tier, outside ag-platform-oncall and apr-sleep-<tier>, and untagged
      # (CAP-AZ-013). The provider deletes the generated rule when it creates the component; the app's alerting is the
      # fast-burn alert of monitoring.tf (ADR-D15).
      disable_generated_rule = true
    }
  }
}
