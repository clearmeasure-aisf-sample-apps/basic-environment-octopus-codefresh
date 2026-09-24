# Committed inputs of terraform/apps/tier for tier nonprod (ADR-IR14), passed by apps-plan and apps-apply as
# -var-file=nonprod.tfvars together with -var=app=#{App.Name}. Identifiers only; nonprod.tfvars.example explains every
# value. The main loop replaces the <placeholders> with the provisioned values at P1-09.

tier            = "nonprod"
tenant_id       = "<AZURE_TENANT_ID>"
subscription_id = "<AZURE_SUBSCRIPTION_ID>"
location        = "<azure-region>"

# Created by the foundation in rg-platform-nonprod-shared; one container <app>-<env> per app-environment with a database.
backup_storage_account_name = "<backup-storage-account-nonprod>"

# §7.0 sizes of disk-<app>-<env>-db.
db_disk_size_gb = {
  tdd  = 8
  uat  = 8
  prod = 32
}

# Empty: the vault firewalls stay open and Entra RBAC decides.
key_vault_allowed_ip_ranges          = []
key_vault_soft_delete_retention_days = 90

# The SLO alerts go live in phase 3 (§9) for nonprod; prod alerts from the start.
slo_alerts_enabled = false

tags = {}
