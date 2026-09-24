# Workload federated credentials of the platform identities (§7.0 "Identities"). The issuer is this cluster's
# OIDC issuer, which exists only after the cluster does and changes when the cluster is rebuilt, so this layer
# owns these credentials; the foundation owns the identities and every grant. Creating a federated credential on
# a managed identity is an ARM write that Contributor on the identity's resource group allows.
#
#   system:serviceaccount:external-secrets:external-secrets             -> id-eso-platform-<tier>
#   system:serviceaccount:kyverno:kyverno-admission-controller          -> id-kyverno-<tier>
#   system:serviceaccount:platform-backup:db-backup                     -> id-db-backup-<tier>
#
# Exact subjects only, one credential per identity (limit 20; ADR-IR34 decision 14), so no write races on one
# identity. App workload identities id-<app>-<env>-app get theirs from terraform/apps/tier, which runs again after
# a rebuild (apps-apply after env-apply).

locals {
  platform_workloads = {
    external-secrets = {
      identity = "id-eso-platform-${var.tier}"
      subject  = "system:serviceaccount:external-secrets:external-secrets"
    }
    kyverno-admission-controller = {
      identity = "id-kyverno-${var.tier}"
      subject  = "system:serviceaccount:kyverno:kyverno-admission-controller"
    }
    db-backup = {
      identity = "id-db-backup-${var.tier}"
      subject  = "system:serviceaccount:platform-backup:db-backup"
    }
  }
}

data "azurerm_user_assigned_identity" "platform_workload" {
  for_each = local.platform_workloads

  name                = each.value.identity
  resource_group_name = local.rg_aks
}

resource "azurerm_federated_identity_credential" "platform_workload" {
  for_each = local.platform_workloads

  name                      = "${local.cluster_name}-${each.key}"
  user_assigned_identity_id = data.azurerm_user_assigned_identity.platform_workload[each.key].id
  audience                  = [local.federation_audience]
  issuer                    = azurerm_kubernetes_cluster.this.oidc_issuer_url
  subject                   = each.value.subject
}
