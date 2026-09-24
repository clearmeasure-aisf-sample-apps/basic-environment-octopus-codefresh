# terraform/apps/grants: the provisioner's per-app layer (ADR-IR34 decisions 2, 3, 11, 12 and 14; §7.0 "Terraform
# layers"). It reads apps/<app>.yaml through ../descriptor for both tiers and creates, only for apps that ask:
#   rg-app-<app>-<tier>          azure.resourceGroup: app-owned Azure services, one group per tier the app runs in
#   id-<app>-<env>-deploy        octopus.azureAccount: in rg-platform-<tier>-apps, with one Octopus-issuer federated
#                                credential per Octopus project, subject space:<space-slug>:project:<project>:
#                                environment:<env> (at most 20); Key Vault Secrets User on its own vault; Contributor
#                                on rg-app-<app>-<tier> when declared. octopus/terraform then creates the OIDC account
#                                azure-<app>-<env> from its client ID.
#   id-<app>-<env>-app           azure.workloadIdentity: in rg-platform-<tier>-apps; azure.roles on rg-app-<app>-<tier>.
#                                Its workload federated credential belongs to terraform/apps/tier (cluster issuer).
#   conformance grants           sp-platform-conformance: Key Vault Secrets Officer on the sandbox tdd vault (canary
#                                secrets, CAP-GIT-007) and Reader on every rg-app-<app>-<tier> (CAP-AZ-013, CAP-AZ-017).
# Nothing reaches another app's resources or another tier: every scope is derived from <app> and the fixed tier map.
#
# Applied only by the provisioner (sp-automation-mvp-sub) from an operator session, never from Octopus or Codefresh:
# it is the only identity that creates role assignments (decision 3). Its constrained RBAC Administrator admits the
# roles used here (Key Vault Secrets User and Officer, Contributor, Reader, Storage Blob Data Contributor) for
# service-principal assignees; every assignment therefore sets principal_type = "ServicePrincipal".
#
# Order: apps-apply in each tier first (the vaults must exist), then this layer, then apps-apply again for apps with
# a workload identity. Apply with -parallelism=1: federated credentials on one identity must be written one at a
# time (409 conflicts otherwise, V12), and for_each cannot chain its own instances.
#
# State: app-grants-<app>.tfstate in container tfstate of <tfstate-storage-account-global> (rg-platform-global):
#   terraform -chdir=terraform/apps/grants init \
#     -backend-config="resource_group_name=rg-platform-global" \
#     -backend-config="storage_account_name=<tfstate-storage-account-global>" \
#     -backend-config="container_name=tfstate" \
#     -backend-config="key=app-grants-<app>.tfstate" \
#     -backend-config="use_azuread_auth=true"
#   terraform -chdir=terraform/apps/grants apply -parallelism=1 -var-file=grants.tfvars -var=app=<app>

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.6"
    }
  }

  backend "azurerm" {}
}

provider "azurerm" {
  subscription_id = var.subscription_id
  tenant_id       = var.tenant_id

  # terraform/foundation registers the resource providers.
  resource_provider_registrations = "none"

  storage_use_azuread = true

  features {}
}
