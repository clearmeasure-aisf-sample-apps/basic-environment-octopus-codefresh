# Committed inputs of terraform/apps/tier for tier prod (ADR-IR14), passed by apps-plan and apps-apply as
# -var-file=prod.tfvars together with -var=app=#{App.Name}. Identifiers only; prod.tfvars.example explains every
# value. The main loop replaces the <placeholders> with the provisioned values at P1-09.

tier            = "prod"
tenant_id       = "40645332-2e20-4bca-882c-b2706f93ce44"
subscription_id = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69"
location        = "southcentralus"

# Created by the foundation in rg-platform-prod-shared; one container <app>-<env> per app-environment with a database.
backup_storage_account_name = "stbkpprodi3aldz"

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
slo_alerts_enabled = true

tags = {}
