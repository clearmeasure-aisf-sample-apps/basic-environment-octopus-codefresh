# terraform/foundation: the global Azure layer of the multi-app platform (ADR-IR34 decisions 2, 3 and
# 25; design §7.0 "Terraform layers").
#
# Applied only by the provisioner (sp-automation-mvp-sub) from an operator session, never from Octopus
# or Codefresh. The provisioner holds Contributor on the subscription, the constrained Role Based Access
# Control Administrator of docs/owner/Grant-ProvisionerRights.ps1 (service-principal and group
# assignees; the role list below) and Graph Group.Create and Application.ReadWrite.OwnedBy. It is the
# only identity that creates role assignments; terraform/build, terraform/tier and terraform/apps/tier
# create none.
#
# Creates: provider registrations, the platform resource groups, the shared registry with its scope
# maps and token objects, the state accounts (global and per tier), the backup accounts (per tier), the
# platform identities with their Octopus-issuer federated credentials, the app registration
# sp-platform-conformance, every platform grant (those of group platform-operators included; the group itself is
# created with az before the first apply, entra.tf) and three budgets.
# Not here (ADR-IR34): Azure SQL, Azure Policy, PIM, and the CanNotDelete locks, which the Owner script
# applies (Grant-ProvisionerRights.ps1 -ApplyLocks). The Log Analytics workspaces log-platform-<tier>
# belong to terraform/tier (ADR-IR34 "Resource groups").
#
# Roles assigned here while conformance_least_privilege = false (default): AcrPull, Reader, Contributor,
# Key Vault Secrets Officer, Key Vault Secrets User, Storage Blob Data Contributor, Network Contributor,
# Managed Identity Operator and Azure Kubernetes Service RBAC Cluster Admin; all are in the Owner
# script's current role list. conformance_least_privilege = true adds Azure Kubernetes Service Cluster
# User Role, Azure Kubernetes Service RBAC Reader and Azure Kubernetes Service RBAC Writer, which the
# provisioner can assign only after the user re-runs the changed Owner script (P1-03). A precondition in
# role-assignments.tf stops the plan before any other role reaches Azure.
#
# State: foundation.tfstate in container tfstate of <tfstate-storage-account-global> (rg-platform-global).
# The first apply creates that account, so it runs on local state and then migrates (P1-02). Run from
# terraform/foundation as the provisioner, with ARM_CLIENT_ID, ARM_CLIENT_SECRET, ARM_TENANT_ID and
# ARM_SUBSCRIPTION_ID exported (both providers and the backend read them):
#   1. cp foundation.tfvars.example foundation.auto.tfvars    # replace every <placeholder>; never commit
#   2. printf 'terraform {\n  backend "local" {}\n}\n' > backend_override.tf
#   3. terraform init -input=false
#      terraform apply -input=false
#   4. rm backend_override.tf
#      terraform init -input=false -migrate-state -force-copy \
#        -backend-config="storage_account_name=sttfglobal<name_suffix>" \
#        -backend-config="container_name=tfstate" \
#        -backend-config="key=foundation.tfstate" \
#        -backend-config="use_azuread_auth=true"
#      The provisioner's Storage Blob Data Contributor on the account is created in step 3; if the
#      migration answers 403 AuthorizationPermissionMismatch, wait two minutes for RBAC propagation and
#      repeat step 4 (the local state stays intact until the copy succeeds).
#   5. terraform plan -input=false -detailed-exitcode        # exit code 0: the migrated state matches
#      rm -f terraform.tfstate terraform.tfstate.backup
# Later runs: steps 1 and 4's init (without -migrate-state -force-copy), then plan and apply.
# The exact account name is in output tfstate.global.storage_account_name.

terraform {
  # Same floor as the other layers (write-only arguments and ephemeral values in terraform/tier and
  # terraform/apps/tier).
  required_version = ">= 1.11.0"

  required_providers {
    # Current majors, checked on the registry 2026-09-24 (§7.10 provider pins).
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.6"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.9"
    }
  }

  # Partial configuration; the values are passed with -backend-config (see above).
  backend "azurerm" {}
}
