# The platform vault of the tier, <kv-platform-<tier>> in rg-platform-<tier>-aks (§7.0, §7.8). Nothing else:
# Azure SQL, the per-environment vaults and their private endpoints left the platform (ADR-IR34). App vaults
# kv-<app>-<e>-<hash4> belong to terraform/apps/tier.
#
# Secrets. This layer writes none. The operator (group platform-operators, Key Vault Secrets Officer from the
# foundation) seeds them at P1-07 and rotates them (docs/runbooks/credential-rotation.md):
#   argocd-repo-read-credential         ESO -> Secret argocd/argocd-repo-creds (the stored PAT until R11)
#   argocd-octopus-gateway-token        ESO -> Secret octopus-argocd-gateway/argocd-octopus-token
#   octopus-gateway-registration-token  ESO -> Secret octopus-argocd-gateway/octopus-gateway-registration
# Readers: id-eso-platform-<tier> (Key Vault Secrets User, foundation) through ClusterSecretStore
# platform-keyvault, whose conditions admit only the argocd and octopus-argocd-gateway namespaces (gitops).
#
# Network: no private endpoint (ADR-IR34 consequences). The data plane is public; Entra RBAC decides. With
# key_vault_allowed_ip_ranges set, the firewall denies everything else and admits the tier's egress IP too.

resource "azurerm_key_vault" "platform" {
  name                = var.platform_key_vault_name
  location            = var.location
  resource_group_name = local.rg_aks
  tenant_id           = var.tenant_id
  sku_name            = "standard"

  rbac_authorization_enabled = true
  purge_protection_enabled   = true
  soft_delete_retention_days = 90

  public_network_access_enabled = true

  network_acls {
    default_action = length(var.key_vault_allowed_ip_ranges) > 0 ? "Deny" : "Allow"
    bypass         = "AzureServices"
    ip_rules       = length(var.key_vault_allowed_ip_ranges) > 0 ? concat(var.key_vault_allowed_ip_ranges, [azurerm_public_ip.egress.ip_address]) : []
  }

  tags = merge(local.base_tags, { "platform-component" = "vault" })
}

# Every secret read, write and denied request.
resource "azurerm_monitor_diagnostic_setting" "platform_vault" {
  name                       = "kv-audit-to-log-platform"
  target_resource_id         = azurerm_key_vault.platform.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id

  enabled_log {
    category = "AuditEvent"
  }
}
