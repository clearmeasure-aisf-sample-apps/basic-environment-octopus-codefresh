# Inputs of one app in one tier: -var-file=<tier>.tfvars (committed, identifiers only) and -var=app=<app>
# (prompted App.Name of apps-plan and apps-apply).

variable "app" {
  description = "App slug; apps/<app>.yaml must exist (ADR-IR34 decision 10)."
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.app))
    error_message = "app must match ^[a-z][a-z0-9]{2,11}$."
  }
}

variable "tier" {
  description = "nonprod (tdd, uat) or prod (prod)."
  type        = string

  validation {
    condition     = contains(["nonprod", "prod"], var.tier)
    error_message = "tier must be nonprod or prod."
  }
}

variable "tenant_id" {
  description = "Entra tenant ID (<AZURE_TENANT_ID>)."
  type        = string
}

variable "subscription_id" {
  description = "Subscription ID (<AZURE_SUBSCRIPTION_ID>); part of the vault-name hash."
  type        = string
}

variable "location" {
  description = "Azure region (<azure-region>)."
  type        = string
}

variable "backup_storage_account_name" {
  description = "Backup account of the tier (<backup-storage-account-<tier>>, created by the foundation in rg-platform-<tier>-shared)."
  type        = string
}

variable "db_disk_size_gb" {
  description = "Size of disk-<app>-<env>-db per environment (§7.0: 8 GiB in tdd and uat, 32 GiB in prod). Growing is an in-place resize; shrinking is refused by Azure."
  type        = map(number)
  default = {
    tdd  = 8
    uat  = 8
    prod = 32
  }
}

variable "key_vault_allowed_ip_ranges" {
  description = "Public ranges allowed to the app vaults' data plane. Empty leaves the firewall open; Entra RBAC decides (the default: ESO, the conformance harness and operators reach the vaults from changing addresses)."
  type        = list(string)
  default     = []
}

variable "key_vault_soft_delete_retention_days" {
  description = "Soft-delete retention of the app vaults (7 to 90 days)."
  type        = number
  default     = 90
}

variable "slo_alerts_enabled" {
  description = "Enable the slo-fast-burn-<app>-<env> alerts of this tier."
  type        = bool
  default     = true
}

variable "slo_alert_environments" {
  description = "Environments of this tier whose slo-fast-burn-<app>-<env> alerts are enabled when slo_alerts_enabled is true. Null enables every environment of the tier; P3 enables uat alone in nonprod (docs/cutover-and-decommission.md)."
  type        = list(string)
  default     = null

  validation {
    condition     = var.slo_alert_environments == null || alltrue([for e in coalesce(var.slo_alert_environments, []) : contains(["tdd", "uat", "prod"], e)])
    error_message = "slo_alert_environments holds tdd, uat or prod only."
  }
}

variable "tags" {
  description = "Extra tags merged into every resource. platform-tier, platform-component, platform-app and platform-env are always set (§7.0)."
  type        = map(string)
  default     = {}
}

variable "descriptor" {
  description = "Test hook: a decoded descriptor used instead of apps/<app>.yaml. Leave null in real runs."
  type        = any
  default     = null
}
