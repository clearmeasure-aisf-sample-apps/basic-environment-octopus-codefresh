# Committed inputs of terraform/apps/tier for tier nonprod (ADR-IR14), passed by apps-plan and apps-apply as
# -var-file=nonprod.tfvars together with -var=app=#{App.Name}. Identifiers only; nonprod.tfvars.example explains every
# value. The main loop replaces the <placeholders> with the provisioned values at P1-09.

tier            = "nonprod"
tenant_id       = "40645332-2e20-4bca-882c-b2706f93ce44"
subscription_id = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69"
location        = "southcentralus"

# Created by the foundation in rg-platform-nonprod-shared; one container <app>-<env> per app-environment with a database.
backup_storage_account_name = "stbkpnonprodi3aldz"

# §7.0 sizes of disk-<app>-<env>-db.
db_disk_size_gb = {
  tdd  = 8
  uat  = 8
  prod = 32
}

# Empty: the vault firewalls stay open and Entra RBAC decides.
key_vault_allowed_ip_ranges          = []
key_vault_soft_delete_retention_days = 90

# The SLO alerts go live in phase 3 (§9) for nonprod, uat only: slo_alerts_enabled = true with
# slo_alert_environments = ["uat"], then apps-apply in infra-nonprod. Prod alerts from the start.
slo_alerts_enabled     = true
slo_alert_environments = ["uat"]

tags = {}
