# The ten platform resource groups of ADR-IR34 decision 2 ("Resource groups (final)"). The provisioner
# creates every group; the tier identities cannot. Other layers only add resources inside them:
#
#   rg-platform-global            state account <tfstate-storage-account-global>
#   rg-platform-build             registry, id-octopus-acr-pull; aks-platform-build (terraform/build)
#   rg-platform-<tier>-shared     state and backup accounts, id-platform-lifecycle-<tier>; network, IPs
#                                 and log-platform-<tier> (terraform/tier)
#   rg-platform-<tier>-aks        cluster identities; aks-platform-<tier>, the platform vault and
#                                 apr-sleep-<tier> (terraform/tier)
#   rg-platform-<tier>-data       database disks (terraform/apps/tier, CSI driver)
#   rg-platform-<tier>-apps       app vaults, App Insights, optional app identities (apps layers)
#
# CanNotDelete locks on rg-platform-global, rg-platform-build and rg-platform-prod-data come from the
# Owner script (docs/owner/Grant-ProvisionerRights.ps1 -ApplyLocks): neither Contributor nor the
# constrained RBAC Administrator may write Microsoft.Authorization/locks (E36).

resource "azurerm_resource_group" "this" {
  for_each = local.resource_groups

  name     = each.key
  location = var.location

  tags = merge(local.base_tags, {
    "platform-tier"      = each.value.tier
    "platform-component" = each.value.component
  })
}
