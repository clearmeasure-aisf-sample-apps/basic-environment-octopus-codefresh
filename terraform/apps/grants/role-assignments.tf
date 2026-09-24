# Every grant of one app (§7.0 "Identities"). Scopes are the app's own vaults (deterministic names from
# ../descriptor; the vaults are created by apps-apply first) and its own rg-app-<app>-<tier>; never a tier group,
# another app's resource or the other tier. CAP-AZ-017 checks the live result.

resource "azurerm_role_assignment" "deploy_vault" {
  for_each = local.deploy_identities

  scope                = local.vault_ids[each.key]
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_user_assigned_identity.deploy[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "${each.value.name}: reads its own vault (Octopus account azure-${var.app}-${each.key})."
}

resource "azurerm_role_assignment" "deploy_group" {
  for_each = { for e, v in local.deploy_identities : e => v if contains(keys(local.resource_groups), v.tier) }

  scope                = azurerm_resource_group.app[each.value.tier].id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.deploy[each.key].principal_id
  principal_type       = "ServicePrincipal"
  description          = "${each.value.name}: manages the app-owned services of rg-app-${var.app}-${each.value.tier}."
}

resource "azurerm_role_assignment" "app_roles" {
  for_each = local.app_role_assignments

  scope                = azurerm_resource_group.app[local.env_tier[each.value.env]].id
  role_definition_name = each.value.role
  principal_id         = azurerm_user_assigned_identity.app[each.value.env].principal_id
  principal_type       = "ServicePrincipal"
  description          = "id-${var.app}-${each.value.env}-app: descriptor role on rg-app-${var.app}-${local.env_tier[each.value.env]}."
}

# --- Conformance principal (§7.0: sp-platform-conformance) -------------------------------------------------------------
# Canary secrets for CAP-GIT-007 are written only to the sandbox tdd vault.
resource "azurerm_role_assignment" "conformance_sandbox_vault" {
  for_each = local.conformance && var.app == "sandbox" && contains(keys(local.vault_ids), "tdd") ? { tdd = local.vault_ids["tdd"] } : {}

  scope                = each.value
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = var.conformance_principal_object_id
  principal_type       = "ServicePrincipal"
  description          = "sp-platform-conformance: canary secrets in the sandbox tdd vault (CAP-GIT-007)."
}

# Reads of app-owned groups for the tag and identity-scope tests (CAP-AZ-013, CAP-AZ-017).
resource "azurerm_role_assignment" "conformance_reader" {
  for_each = local.conformance ? local.resource_groups : {}

  scope                = azurerm_resource_group.app[each.key].id
  role_definition_name = "Reader"
  principal_id         = var.conformance_principal_object_id
  principal_type       = "ServicePrincipal"
  description          = "sp-platform-conformance: reads rg-app-${var.app}-${each.key}."
}
