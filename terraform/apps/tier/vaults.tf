# One vault per app-environment, kv-<app>-<e>-<hash4> in rg-platform-<tier>-apps (ADR-IR34 decision 8, §7.8).
#
# Readers and writers, all granted by the foundation or terraform/apps/grants, never here:
#   id-eso-platform-<tier>            Key Vault Secrets User on rg-platform-<tier>-apps; reads through
#                                     ClusterSecretStore <app>-<env>, which admits only that app-environment's
#                                     namespaces and platform-backup (decision 6)
#   id-platform-lifecycle-<tier>      Key Vault Secrets Officer on the group: this layer and rotate-db-passwords
#   platform-operators                Key Vault Secrets Officer: replaces stand-ins, rotates by hand
#   id-<app>-<env>-deploy             Key Vault Secrets User on its own vault (grants; optional)
#   sp-platform-conformance           Key Vault Secrets Officer on the sandbox tdd vault only (grants)
#
# Values: generated passwords come from ephemeral random_password resources and reach Azure through the write-only
# value_wo, so they never enter plan or state. value_wo_version stays 1: Terraform writes each value once and never
# reverts a rotation (rotate-db-passwords, docs/runbooks/credential-rotation.md). SQL logins, not Entra: SQL Server
# containers have no Entra authentication (ADR-IR34 decision 8); the NetworkPolicy of the tenant fences them.

resource "azurerm_key_vault" "app" {
  for_each = local.envs

  name                = module.descriptor.vault_names[each.key]
  location            = var.location
  resource_group_name = local.rg_apps
  tenant_id           = var.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = var.key_vault_soft_delete_retention_days

  public_network_access_enabled = true

  network_acls {
    default_action = length(var.key_vault_allowed_ip_ranges) > 0 ? "Deny" : "Allow"
    bypass         = "AzureServices"
    ip_rules       = var.key_vault_allowed_ip_ranges
  }

  tags = merge(local.env_tags[each.key], { "platform-component" = "app-vault" })
}

resource "azurerm_monitor_diagnostic_setting" "vault" {
  for_each = local.envs

  name                       = "kv-audit-to-log-platform"
  target_resource_id         = azurerm_key_vault.app[each.key].id
  log_analytics_workspace_id = data.azurerm_log_analytics_workspace.tier.id

  enabled_log {
    category = "AuditEvent"
  }
}

# --- Database logins: db-sa-password, db-migrator-password, db-app-password ---------------------------------------
# SQL Server requires three of four character classes; these have all four. The specials avoid characters that
# need quoting in connection strings, shells or URLs.

ephemeral "random_password" "db" {
  for_each = local.db_secrets

  length           = 32
  min_upper        = 2
  min_lower        = 2
  min_numeric      = 2
  min_special      = 2
  override_special = "-_."
}

resource "azurerm_key_vault_secret" "db" {
  for_each = local.db_secrets

  name             = "db-${each.value.login}-password"
  key_vault_id     = azurerm_key_vault.app[each.value.env].id
  value_wo         = ephemeral.random_password.db[each.key].result
  value_wo_version = 1
  content_type     = "password; SQL login ${local.db_logins[each.value.login]}; rotated by rotate-db-passwords"
}

# --- App keys from the descriptor (secrets[]) ------------------------------------------------------------------------
# generate: true writes a random value once. Otherwise the key starts with an obvious non-secret stand-in so the app's
# ExternalSecret can sync before the real value exists; a platform-operators member replaces it
# (docs/runbooks/credential-rotation.md) and Terraform never overwrites it.

ephemeral "random_password" "app_secret" {
  for_each = local.app_secrets

  length  = 48
  special = false
}

resource "azurerm_key_vault_secret" "app" {
  for_each = local.app_secrets

  name             = each.value.name
  key_vault_id     = azurerm_key_vault.app[each.value.env].id
  value_wo         = each.value.generate ? ephemeral.random_password.app_secret[each.key].result : "not-set-see-credential-rotation-runbook"
  value_wo_version = 1
  content_type     = each.value.generate ? "generated once by terraform/apps/tier" : "set by platform-operators; stand-in until then"
}
