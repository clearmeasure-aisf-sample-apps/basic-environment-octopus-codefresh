# The one shared registry <acr-name> in rg-platform-build (ADR-IR34 decision 5, §7.0 "Registry and
# supply chain"): Standard, no admin user, no anonymous pull. Repositories apps/<app>/<image>,
# apps-previews/<app>/<image> and platform/<image>.
#
# Readers use Entra ID and AcrPull (role-assignments.tf): the tier kubelet and Kyverno identities, the
# Octopus feed identity id-octopus-acr-pull and sp-platform-conformance. The build cluster holds no
# grant here (CAP-CF-012): the Codefresh engine pulls step images such as platform/ci-dotnet with the
# cf-platform-pull token through registry integration acr-platform-pull (ADR-IR19).
#
# Codefresh cannot federate to Entra (E15, E37), so it uses repository-scoped tokens. This layer
# creates only the scope maps and the token objects, never a token password
# (azurerm_container_registry_token_password would store it in state). P1-05 generates each password
# by CLI with a 90-day expiry, for example:
#   az acr token credential generate --registry <acr-name> --name cf-apps-release \
#     --password1 --expiration-in-days 90 --query 'passwords[0].value' --output tsv
# and stores it only in the Codefresh registry integration or context named in local.acr_tokens.

resource "azurerm_container_registry" "this" {
  name                = local.acr_name
  resource_group_name = azurerm_resource_group.this[local.rg_build].name
  location            = var.location
  sku                 = "Standard"

  admin_enabled          = false
  anonymous_pull_enabled = false

  # Standard cannot restrict network access (Premium only); Codefresh SaaS, Octopus Cloud and both
  # clusters reach the registry over the internet (ADR-IR34 "SKU decision").
  public_network_access_enabled = true

  # AcrPull and the scope-map tokens apply only in this mode; the ABAC repository mode would ignore
  # AcrPull.
  role_assignment_mode = "LegacyRegistryPermissions"

  tags = merge(local.base_tags, {
    "platform-tier"      = "build"
    "platform-component" = "registry"
  })

  lifecycle {
    # Every image of every app lives here; a rename (name_suffix) must never replace it.
    prevent_destroy = true
  }
}

locals {
  # Token => repository prefixes, access set and the Codefresh object that holds its password (§7.0
  # "Registry and supply chain", "Codefresh"). A wildcard must be a single trailing prefix/* (V: ACR
  # tokens, all service tiers); it matches nested repositories such as apps/<app>/<image> [VERIFY].
  acr_tokens = {
    "cf-apps-release"       = { repositories = ["apps/*"], access = "push", holder = "registry integration acr-apps-release; context platform-registry" }
    "cf-apps-preview"       = { repositories = ["apps-previews/*"], access = "push", holder = "registry integration acr-apps-preview" }
    "cf-platform-ci"        = { repositories = ["platform/*"], access = "push", holder = "registry integration acr-platform-ci" }
    "cf-platform-pull"      = { repositories = ["platform/*"], access = "pull", holder = "registry integration acr-platform-pull" }
    "cf-platform-retention" = { repositories = ["apps/*", "apps-previews/*"], access = "retention", holder = "context platform-registry-retention" }
  }

  # push: content and metadata read and write, no delete. metadata/write covers the tag lock after
  # signing (az acr repository update --write-enabled false) [VERIFY V04, Q6].
  # retention: read, delete and metadata write, so it can unlock, then delete, expired tags.
  acr_actions = {
    push      = ["content/read", "content/write", "metadata/read", "metadata/write"]
    pull      = ["content/read", "metadata/read"]
    retention = ["content/read", "content/delete", "metadata/read", "metadata/write"]
  }
}

resource "azurerm_container_registry_scope_map" "this" {
  for_each = local.acr_tokens

  name                    = "${each.key}-scope"
  container_registry_name = azurerm_container_registry.this.name
  resource_group_name     = azurerm_container_registry.this.resource_group_name
  description             = "${each.value.access} on ${join(", ", each.value.repositories)} for token ${each.key} (${each.value.holder})"
  actions = flatten([
    for repository in each.value.repositories : [
      for action in local.acr_actions[each.value.access] : "repositories/${repository}/${action}"
    ]
  ])
}

resource "azurerm_container_registry_token" "this" {
  for_each = local.acr_tokens

  name                    = each.key
  container_registry_name = azurerm_container_registry.this.name
  resource_group_name     = azurerm_container_registry.this.resource_group_name
  scope_map_id            = azurerm_container_registry_scope_map.this[each.key].id
  enabled                 = true
}
