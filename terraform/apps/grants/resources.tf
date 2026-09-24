# Groups and identities of one app. Names are fixed by §7.0; the tier groups rg-platform-<tier>-apps belong to the
# foundation.

resource "azurerm_resource_group" "app" {
  for_each = local.resource_groups

  name     = each.value
  location = var.location

  tags = merge(local.base_tags, {
    "platform-tier"      = each.key
    "platform-component" = "app-services"
    "platform-env"       = join("+", [for e, t in local.env_tier : e if t == each.key])
  })
}

resource "azurerm_user_assigned_identity" "deploy" {
  for_each = local.deploy_identities

  name                = each.value.name
  location            = var.location
  resource_group_name = "rg-platform-${each.value.tier}-apps"

  tags = merge(local.base_tags, {
    "platform-tier"      = each.value.tier
    "platform-component" = "app-identity"
    "platform-env"       = each.key
  })
}

resource "azurerm_user_assigned_identity" "app" {
  for_each = local.app_identities

  name                = each.value.name
  location            = var.location
  resource_group_name = "rg-platform-${each.value.tier}-apps"

  tags = merge(local.base_tags, {
    "platform-tier"      = each.value.tier
    "platform-component" = "app-identity"
    "platform-env"       = each.key
  })
}

# Octopus-issuer credentials of the deploy identities. The Octopus account azure-<app>-<env> uses subject keys space,
# project and environment, so a foreign project that selects the account presents its own slug and Entra refuses it.
# Apply with -parallelism=1 (versions.tf): several credentials on one identity must not be written concurrently.
resource "azurerm_federated_identity_credential" "deploy" {
  for_each = local.deploy_credentials

  name                      = "octopus-${each.value.project}-${each.value.env}"
  user_assigned_identity_id = azurerm_user_assigned_identity.deploy[each.value.env].id
  audience                  = ["api://AzureADTokenExchange"]
  issuer                    = var.octopus_url
  subject                   = "space:${var.octopus_space_slug}:project:${each.value.project}:environment:${each.value.env}"
}
