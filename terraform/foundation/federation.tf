# Octopus-issuer federated credentials (§7.0 "Identities", §5.4). Created here in advance; Contributor
# may write them, but no tier identity can reach rg-platform-build or another tier's -shared group.
# octopus/terraform creates the matching accounts azure-platform-lifecycle-<tier> (subject keys space,
# project, environment) and the feed acr-apps.
#
#   issuer    <OCTOPUS_URL>, no trailing slash (E21)
#   audience  api://AzureADTokenExchange
#   subjects  space:<space-slug>:project:platform-infrastructure:environment:infra-<tier>
#             space:<space-slug>:feed:acr-apps   [VERIFY format: copy the subject from a failed token
#                                                 exchange if it differs, V: P1-06]
#
# The map is keyed by identity, so each identity carries exactly one Octopus credential and never
# receives two credential writes at once (concurrent writes to one identity fail with 409, V12). A
# second credential on the same identity goes into its own resource with depends_on on the first. The
# Kubernetes workload credentials of the -aks identities come from terraform/tier; no identity here
# receives credentials from two layers.

locals {
  octopus_federations = merge(
    {
      for t in local.tiers : "id-platform-lifecycle-${t}" => {
        credential  = "octopus-platform-infrastructure-infra-${t}"
        identity_id = azurerm_user_assigned_identity.tier["id-platform-lifecycle-${t}"].id
        subject     = "space:${var.octopus_space_slug}:project:platform-infrastructure:environment:infra-${t}"
      }
    },
    {
      "id-octopus-acr-pull" = {
        credential  = "octopus-feed-acr-apps"
        identity_id = azurerm_user_assigned_identity.octopus_acr_pull.id
        subject     = "space:${var.octopus_space_slug}:feed:acr-apps"
      }
    },
  )
}

resource "azurerm_federated_identity_credential" "octopus" {
  for_each = local.octopus_federations

  name                      = each.value.credential
  user_assigned_identity_id = each.value.identity_id
  audience                  = [local.federation_audience]
  issuer                    = local.octopus_issuer
  subject                   = each.value.subject
}

# GitHub-issuer credential of id-dashboard-status: only the workflows of the dashboard repository's main branch may
# sign in as it. Its own resource, on its own identity: no identity receives two credential writes at once (V12).
resource "azurerm_federated_identity_credential" "dashboard_status" {
  name                      = "github-dashboard-main"
  user_assigned_identity_id = azurerm_user_assigned_identity.dashboard_status.id
  audience                  = [local.federation_audience]
  issuer                    = "https://token.actions.githubusercontent.com"
  subject                   = local.dashboard_subject
}
