# ACR <acr-name> and the repository scope maps for Codefresh (§5.2, ADR-D8, R10).
#
# Codefresh OIDC cannot federate to Entra (E15, E37), so Codefresh pushes with repository-scoped
# ACR tokens held as Codefresh registry integrations. This layer creates only the scope maps and
# token objects. It never creates token passwords: azurerm_container_registry_token_password
# would put them in state. A person generates each password with a 90-day expiry and pastes it
# into the Codefresh registry integration (docs/runbooks/credential-rotation.md).
#
# Every pull identity (kubelet, Kyverno, Octopus feed) uses AcrPull through Entra; the admin
# user stays disabled.

resource "azurerm_container_registry" "this" {
  name                = var.acr_name
  resource_group_name = azurerm_resource_group.this[local.rg_shared].name
  location            = var.location
  sku                 = var.acr_sku

  admin_enabled          = false
  anonymous_pull_enabled = false

  # Standard cannot disable public access; Premium with private endpoints is Q6.
  public_network_access_enabled = true

  tags = var.tags
}

locals {
  # Token => repository pattern, Codefresh registry integration and access (§5.2, ADR-IR19).
  # Wildcard repository scopes in scope maps are [VERIFY] on the Standard SKU.
  acr_tokens = {
    "cf-workorders-release" = { repositories = "workorders/*", integration = "acr-workorders-release", access = "push" }
    "cf-workorders-preview" = { repositories = "workorders-previews/*", integration = "acr-workorders-preview", access = "push" }
    "cf-platform-ci"        = { repositories = "platform/*", integration = "acr-platform-ci", access = "push" }
    # Pull-only: the step image of workorders/ci, workorders/release and workorders/preview. Only
    # workorders/ci-image uses the push token for platform/*.
    "cf-platform-pull" = { repositories = "platform/*", integration = "acr-platform-pull", access = "pull" }
  }

  # Push, read back (cosign and the digest lookup) and metadata write. Metadata write covers the
  # tag lock (`az acr repository update --write-enabled false`) that supply-chain.sh applies
  # [VERIFY that a token can lock tags, Q6]. No content/delete: CI never deletes images.
  acr_actions = {
    push = ["content/read", "content/write", "metadata/read", "metadata/write"]
    pull = ["content/read", "metadata/read"]
  }
}

resource "azurerm_container_registry_scope_map" "codefresh" {
  for_each = local.acr_tokens

  name                    = "${each.key}-scope"
  container_registry_name = azurerm_container_registry.this.name
  resource_group_name     = azurerm_resource_group.this[local.rg_shared].name
  description             = "${title(each.value.access)} scope for Codefresh registry integration ${each.value.integration}"
  actions                 = [for a in local.acr_actions[each.value.access] : "repositories/${each.value.repositories}/${a}"]
}

resource "azurerm_container_registry_token" "codefresh" {
  for_each = local.acr_tokens

  name                    = each.key
  container_registry_name = azurerm_container_registry.this.name
  resource_group_name     = azurerm_resource_group.this[local.rg_shared].name
  scope_map_id            = azurerm_container_registry_scope_map.codefresh[each.key].id
  enabled                 = true
}
