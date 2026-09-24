# terraform/tier: one app-cluster tier per state (ADR-IR34 decisions 2, 3 and 9; design §7.0 "Terraform layers").
#
# <tier> is nonprod (tdd, uat, previews) or prod (prod). The map is fixed; descriptors cannot change it.
#
# Applied only by the Octopus project platform-infrastructure: runbooks env-plan and env-apply in infra-nonprod
# and infra-prod, env-destroy in infra-nonprod only. They run on Octopus dynamic workers (pool hosted-ubuntu) as
# id-platform-lifecycle-<tier> through OIDC (account azure-platform-lifecycle-<tier>). That identity holds
# Contributor on rg-platform-<tier>-{shared,aks,data,apps}, AKS RBAC Cluster Admin on rg-platform-<tier>-aks,
# Key Vault Secrets Officer on -aks and -apps, and Storage Blob Data Contributor on the tier state account.
# It holds no RBAC Administrator: terraform/foundation (the provisioner) makes every grant, so this layer makes
# none. The boundary lint enforces that.
#
# Foundation objects this layer finds by their §7.0 names (no copied resource IDs):
#   resource groups  rg-platform-<tier>-shared, rg-platform-<tier>-aks, rg-platform-<tier>-apps
#   identities       id-aks-<tier>-controlplane, id-aks-<tier>-kubelet, id-eso-platform-<tier>,
#                    id-kyverno-<tier>, id-db-backup-<tier> (all in rg-platform-<tier>-aks)
#   grants           Network Contributor (-shared), Managed Identity Operator (kubelet identity) and
#                    Contributor (-data) for the control-plane identity; AcrPull for the kubelet and Kyverno
#                    identities. They must exist before the first apply (Q17).
# Every other input comes from <tier>.tfvars next to this file (committed by pull request, ADR-IR14:
# identifiers and sizing, never a secret) and from two ephemeral TF_VAR_* values (variables.tf).
#
# State: tier-<tier>.tfstate in container tfstate of <tfstate-storage-account-<tier>> in
# rg-platform-<tier>-shared. The runbooks pass:
#   -backend-config="resource_group_name=rg-platform-<tier>-shared"
#   -backend-config="storage_account_name=<tfstate-storage-account-<tier>>"
#   -backend-config="container_name=tfstate"
#   -backend-config="key=tier-<tier>.tfstate"
#   -backend-config="use_azuread_auth=true"
# Octopus variable substitution in *.tf files stays off (E29).
#
# Power state (ADR-IR33, docs/runbooks/sleep-and-wake.md). Both app clusters sleep by default; only the runbooks
# env-wake and env-sleep start and stop them. This layer never changes the power state and does not drift while
# a cluster is stopped:
#   - azurerm 5.6 has no power-state argument on azurerm_kubernetes_cluster or its node pools;
#   - node counts, which differ after a start, and the `enabled` flag of apr-sleep-<tier> are in ignore_changes;
#   - the network, the vault, the workspace and the alert objects do not depend on the cluster running.
# A run against a stopped cluster fails instead of changing anything: the Kubernetes and Helm providers cannot
# reach the API server to refresh bootstrap.tf, and AKS accepts only start and delete on a stopped cluster. The
# env-* runbooks therefore run env-wake first (step wake-environment); they skip it while no cluster exists.
# Never run this layer by hand against a sleeping cluster.
#
# Rebuild (env-destroy, infra-nonprod only; CAP-AZ-007, CAP-AZ-008). env-destroy removes the cluster and what hangs
# off it, never the tier's addresses or logs. Its plan and destroy steps pass
#   -target=azurerm_kubernetes_cluster.this
# which also takes the apps pool, the cluster's diagnostic setting, the platform workload federated credentials and
# every Kubernetes and Helm resource (their providers depend on the cluster; checked with Terraform 1.16). The static
# IPs keep the apps domain <ingress-ip-dashed-<tier>>.sslip.io and the egress allow-lists; the workspace keeps the
# audit logs and every appi-<app>-<env>. Both IPs and the workspace carry prevent_destroy, so an untargeted destroy
# fails instead of losing them. The vault, the network and the alert objects stay as well. The next env-apply
# recreates the cluster, and apps-apply per app re-federates the app identities to the new OIDC issuer.

terraform {
  # 1.11+: write-only arguments (data_wo, set_wo) and ephemeral variables keep bootstrap tokens out of state.
  required_version = ">= 1.11.0"

  required_providers {
    # Current majors, checked on the registry 2026-09-24 (§7.10 provider pins).
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.6"
    }
    helm = {
      source  = "hashicorp/helm"
      version = "~> 3.3"
    }
    kubernetes = {
      source  = "hashicorp/kubernetes"
      version = "~> 3.2"
    }
  }

  backend "azurerm" {}
}
