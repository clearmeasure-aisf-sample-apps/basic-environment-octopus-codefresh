# Outputs of one app in one tier. None is secret. CAP-KIT-003 compares vault and disk names with the tenant chart.

output "environments" {
  description = "The app's environments in this tier."
  value       = module.descriptor.environments
}

output "vault_names" {
  description = "kv-<app>-<e>-<hash4> per environment."
  value       = { for e, v in azurerm_key_vault.app : e => v.name }
}

output "vault_uris" {
  description = "Vault URI per environment (ClusterSecretStore <app>-<env>)."
  value       = { for e, v in azurerm_key_vault.app : e => v.vault_uri }
}

output "database_disk_ids" {
  description = "Resource ID of disk-<app>-<env>-db per environment (static PersistentVolume volumeHandle); empty without a database."
  value       = { for e, d in azurerm_managed_disk.db : e => d.id }
}

output "backup_containers" {
  description = "Backup container per environment in <backup-storage-account-<tier>>; empty without a database."
  value       = { for e, c in azurerm_storage_container.backup : e => c.name }
}

output "app_insights_names" {
  description = "appi-<app>-<env> per environment."
  value       = { for e, a in azurerm_application_insights.app : e => a.name }
}

output "workload_identity" {
  description = "Per environment: federated (credential and azure-client-id written), pending (grants has not created the identity yet) or none."
  value = {
    for e in local.envs : e => (
      !module.descriptor.workload_identity ? "none" :
      contains(keys(data.azurerm_user_assigned_identity.app), e) ? "federated" : "pending"
    )
  }
}
