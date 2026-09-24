# terraform/apps/tier: the Azure objects of one app in one tier (ADR-IR34 decisions 8, 11 and 12; design §7.0
# "Terraform layers"), read from apps/<app>.yaml through ../descriptor. Per environment of the app in the tier:
#   vault kv-<app>-<e>-<hash4> (rg-platform-<tier>-apps) with write-only generated values:
#     db-sa-password, db-migrator-password, db-app-password   when the app declares a database
#     secrets[] of the descriptor                              random once, or a stand-in for an operator
#     appinsights-connection-string                            always
#     azure-client-id                                          when id-<app>-<env>-app exists (decision 11)
#   disk disk-<app>-<env>-db (rg-platform-<tier>-data; Standard SSD, no zone; 8 GiB in tdd and uat, 32 GiB in prod)
#   backup container <app>-<env> in <backup-storage-account-<tier>>
#   appi-<app>-<env> and slo-fast-burn-<app>-<env> (rg-platform-<tier>-apps) to log-platform-<tier> and
#     ag-platform-oncall
#   the federated credential of id-<app>-<env>-app for system:serviceaccount:<app>-<env>:<serviceAccount>
#
# Applied only by the runbooks apps-plan and apps-apply of platform-infrastructure (prompted App.Name) in
# infra-<tier>, as id-platform-lifecycle-<tier> (OIDC). This layer needs no running cluster: it reads the cluster's
# OIDC issuer through Azure Resource Manager, and only for apps with a workload identity [VERIFY on a stopped
# cluster; fallback: wake first]. It makes no role assignment; terraform/apps/grants (the provisioner) makes them.
#
# Order for an app with Azure access: apps-apply (vaults exist) -> terraform/apps/grants (identities, grants on the
# vaults) -> apps-apply again (the workload identity's federated credential and azure-client-id). Until the identity
# exists, apps-apply skips those two objects instead of failing. After env-apply rebuilds a cluster, apps-apply runs
# again for every app with a workload identity (new issuer).
#
# State: apps-<app>.tfstate in container tfstate of <tfstate-storage-account-<tier>> (one state per app, decision 12):
#   -backend-config="resource_group_name=rg-platform-<tier>-shared"
#   -backend-config="storage_account_name=<tfstate-storage-account-<tier>>"
#   -backend-config="container_name=tfstate"
#   -backend-config="key=apps-<app>.tfstate"
#   -backend-config="use_azuread_auth=true"
# Inputs: -var-file=<tier>.tfvars (committed next to this file) and -var=app=<app>.

terraform {
  # 1.11+: ephemeral resources and write-only arguments keep generated passwords out of plan and state.
  required_version = ">= 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.6"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.9"
    }
  }

  backend "azurerm" {}
}
