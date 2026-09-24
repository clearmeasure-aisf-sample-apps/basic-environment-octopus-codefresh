# Database storage of an app that declares `database` (§7.0 "Database", directive §§4 and 11).
#
# disk-<app>-<env>-db lives in rg-platform-<tier>-data, outside the AKS-managed node group, so it survives sleep,
# a cluster rebuild (CAP-AZ-008) and the deletion of its namespace. The tenant chart binds it through a static
# PersistentVolume by its resource ID:
#   /subscriptions/<AZURE_SUBSCRIPTION_ID>/resourceGroups/rg-platform-<tier>-data/providers/Microsoft.Compute/disks/disk-<app>-<env>-db
# Standard SSD, locally redundant, no zone: the app pools have no zones, so any node can attach it. The CSI driver
# acts as id-aks-<tier>-controlplane, which holds Contributor on the data group (foundation). The Owner script's
# CanNotDelete lock covers rg-platform-prod-data. An empty disk is formatted on its first mount (fsType of the PV).
#
# Backups (uat and prod CronJobs of the tenant chart, as id-db-backup-<tier>) and restores use container <app>-<env>
# of <backup-storage-account-<tier>>; one container per app-environment with a database, tdd included for ad-hoc
# backups. The account (no shared keys, soft delete, lifecycle rules) belongs to the foundation.

resource "azurerm_managed_disk" "db" {
  for_each = module.descriptor.has_database ? local.envs : toset([])

  name                 = "disk-${var.app}-${each.key}-db"
  location             = var.location
  resource_group_name  = local.rg_data
  storage_account_type = "StandardSSD_LRS"
  create_option        = "Empty"
  disk_size_gb         = var.db_disk_size_gb[each.key]

  tags = merge(local.env_tags[each.key], { "platform-component" = "app-database" })
}

data "azurerm_storage_account" "backup" {
  count = module.descriptor.has_database ? 1 : 0

  name                = var.backup_storage_account_name
  resource_group_name = local.rg_shared
}

resource "azurerm_storage_container" "backup" {
  for_each = module.descriptor.has_database ? local.envs : toset([])

  name                  = "${var.app}-${each.key}"
  storage_account_id    = data.azurerm_storage_account.backup[0].id
  container_access_type = "private"

  metadata = {
    platform_app = var.app
    platform_env = each.key
  }
}
