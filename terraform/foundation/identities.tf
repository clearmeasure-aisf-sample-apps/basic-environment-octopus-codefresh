# Platform user-assigned identities (§7.0 "Identities"). They outlive cluster rebuilds; only their
# workload federated credentials change, which terraform/tier re-creates with the new cluster issuer.
#
# Placement (ADR-IR34 "Resource groups") decides who can add federated credentials to an identity:
#   rg-platform-build            id-octopus-acr-pull. No tier identity can write here.
#   rg-platform-<tier>-shared    id-platform-lifecycle-<tier>, which runs the tier's automation. It holds
#                                Contributor on this group, so it could federate itself to another issuer;
#                                accepted with the ADR placement (single operator, §13).
#   rg-platform-<tier>-aks       cluster and platform workload identities. terraform/tier finds them by
#                                name and adds their cluster-issuer credentials:
#                                  id-eso-platform-<tier>  system:serviceaccount:external-secrets:external-secrets
#                                  id-kyverno-<tier>       system:serviceaccount:kyverno:kyverno-admission-controller
#                                  id-db-backup-<tier>     system:serviceaccount:platform-backup:db-backup
# Per-app identities (id-<app>-<env>-deploy, id-<app>-<env>-app) belong to terraform/apps/grants.

locals {
  # Tier identities: name => tier, resource group key in local.rg_tier, and role in the grants.
  tier_identities = merge([
    for t in local.tiers : {
      "id-platform-lifecycle-${t}" = { tier = t, group = "shared", role = "lifecycle" }
      "id-aks-${t}-controlplane"   = { tier = t, group = "aks", role = "controlplane" }
      "id-aks-${t}-kubelet"        = { tier = t, group = "aks", role = "kubelet" }
      "id-eso-platform-${t}"       = { tier = t, group = "aks", role = "eso" }
      "id-kyverno-${t}"            = { tier = t, group = "aks", role = "kyverno" }
      "id-db-backup-${t}"          = { tier = t, group = "aks", role = "db_backup" }
    }
  ]...)

  # tier => role => identity name, for grants and outputs.
  tier_identity_names = {
    for t in local.tiers : t => {
      for name, i in local.tier_identities : i.role => name if i.tier == t
    }
  }
}

resource "azurerm_user_assigned_identity" "tier" {
  for_each = local.tier_identities

  name                = each.key
  location            = var.location
  resource_group_name = azurerm_resource_group.this[local.rg_tier[each.value.tier][each.value.group]].name

  tags = merge(local.base_tags, {
    "platform-tier"      = each.value.tier
    "platform-component" = "identity"
  })
}

# Octopus feed acr-apps (OIDC); AcrPull on the registry.
resource "azurerm_user_assigned_identity" "octopus_acr_pull" {
  name                = "id-octopus-acr-pull"
  location            = var.location
  resource_group_name = azurerm_resource_group.this[local.rg_build].name

  tags = merge(local.base_tags, {
    "platform-tier"      = "build"
    "platform-component" = "identity"
  })
}
