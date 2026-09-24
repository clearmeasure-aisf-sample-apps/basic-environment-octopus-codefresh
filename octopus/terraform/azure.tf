# Read-only Azure lookups (ADR-IR34 §11.7.2 item 6): the client IDs of the user-assigned identities behind the OIDC
# accounts and the feed. Found by name where terraform/foundation and terraform/apps/grants create them (§7.0):
#   id-platform-lifecycle-<tier>   rg-platform-<tier>-shared   accounts azure-platform-lifecycle-<tier> (strict)
#   id-octopus-acr-pull            rg-platform-build           feed acr-apps (strict)
#   id-<app>-<env>-deploy          rg-platform-<tier>-apps     accounts azure-<app>-<env> (tolerant, see below)
# Nothing here creates, changes or grants anything in Azure.

data "azurerm_user_assigned_identity" "lifecycle" {
  for_each = local.tier_environments

  name                = "id-platform-lifecycle-${each.key}"
  resource_group_name = "rg-platform-${each.key}-shared"
}

data "azurerm_user_assigned_identity" "acr_pull" {
  name                = "id-octopus-acr-pull"
  resource_group_name = "rg-platform-build"
}

# Deploy identities exist only once terraform/apps/grants has run for the app (P1-09 and each onboarding), which comes
# after the app's Octopus shells. The groups are listed first and only existing identities are read; an account whose
# identity is missing gets a placeholder client ID (apps.tf), so the app's OCL, which names the account, loads. The
# re-apply after terraform/apps/grants sets the real ID in place (check app_deploy_identities_found).
data "azurerm_resources" "deploy_identities" {
  for_each = toset(distinct([for key, a in local.app_accounts : local.environment_tiers[a.environment]]))

  resource_group_name = "rg-platform-${each.key}-apps"
  type                = "Microsoft.ManagedIdentity/userAssignedIdentities"
}

locals {
  existing_deploy_identities = toset(flatten([
    for tier, listing in data.azurerm_resources.deploy_identities : [for r in listing.resources : r.name]
  ]))

  deploy_identity_lookups = {
    for key, a in local.app_accounts : key => a if contains(local.existing_deploy_identities, "id-${key}-deploy")
  }
}

data "azurerm_user_assigned_identity" "deploy" {
  for_each = local.deploy_identity_lookups

  name                = "id-${each.key}-deploy"
  resource_group_name = "rg-platform-${local.environment_tiers[each.value.environment]}-apps"
}

locals {
  app_deploy_client_ids = { for key, identity in data.azurerm_user_assigned_identity.deploy : key => identity.client_id }
}

check "app_deploy_identities_found" {
  assert {
    condition     = length(setsubtract(toset(keys(local.app_accounts)), toset(keys(local.app_deploy_client_ids)))) == 0
    error_message = "No deploy identity yet for ${join(", ", sort(tolist(setsubtract(toset(keys(local.app_accounts)), toset(keys(local.app_deploy_client_ids))))))}: those accounts carry a placeholder client ID until terraform/apps/grants has run for the app and this configuration is applied again."
  }
}

# <apps-domain-<tier>> (ADR-IR34 decision 21): <ingress-ip-dashed-<tier>>.sslip.io from the tier's static ingress IP
# pip-platform-<tier>-ingress, which terraform/tier creates in rg-platform-<tier>-shared at env-apply (P1-07, P1-08).
# Until it exists the tier has no domain and Platform.AppsDomain stays unset; the re-apply after env-apply picks it up.
# var.apps_domains overrides a tier (a custom domain, R35).
data "azurerm_resources" "ingress_ip" {
  for_each = local.tier_environments

  resource_group_name = "rg-platform-${each.key}-shared"
  type                = "Microsoft.Network/publicIPAddresses"
  name                = "pip-platform-${each.key}-ingress"
}

data "azurerm_public_ip" "ingress" {
  for_each = { for tier, listing in data.azurerm_resources.ingress_ip : tier => listing if length(listing.resources) > 0 }

  name                = "pip-platform-${each.key}-ingress"
  resource_group_name = "rg-platform-${each.key}-shared"
}

locals {
  apps_domains = merge(
    { for tier, ip in data.azurerm_public_ip.ingress : tier => "${replace(ip.ip_address, ".", "-")}.sslip.io" if ip.ip_address != "" },
    var.apps_domains,
  )
}
