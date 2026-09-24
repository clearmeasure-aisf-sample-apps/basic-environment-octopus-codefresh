# terraform/foundation: the privileged Azure layer (ADR-D10, design §7.10).
#
# Applied as the provisioner service principal (Azure Runtime Provisioner) since the user ran
# docs/owner/Grant-ProvisionerRights.ps1 (R6): Contributor plus Role Based Access Control
# Administrator limited to the ten roles this layer assigns, and Graph Group.Create and
# Application.ReadWrite.OwnedBy for the Entra groups and the Argo CD app registration. The user,
# or someone the user names, signs in as the provisioner; Octopus never applies this layer.
# It holds every role assignment, lock, policy assignment, managed identity and Octopus-issuer
# federated credential, because Contributor alone cannot write Microsoft.Authorization/* (E36).
# Owner-only resources (ADR-D10 status, E52): the CanNotDelete locks and the Azure Policy
# assignments (governance.tf) and the PIM-eligible assignments (role-assignments.tf) need an
# Owner or User Access Administrator. Two passes on the same state until they move to a separate
# root (terraform/foundation-owner, a recommended split): the provisioner applies first and stops
# with authorization errors on exactly those resources; an Owner then applies the same
# configuration. Later provisioner applies only read them (*/read), so they show no changes.
#
# State: foundation.tfstate in container `tfstate` of <tfstate-storage-account>
# (rg-workorders-shared). Bootstrap: the first apply creates that account, so it runs with local
# state, then migrates:
#   1. Create an untracked backend_override.tf containing: terraform { backend "local" {} }
#   2. terraform init && terraform apply -var-file=foundation.tfvars
#   3. Delete backend_override.tf, then run:
#        terraform init -migrate-state \
#          -backend-config="resource_group_name=rg-workorders-shared" \
#          -backend-config="storage_account_name=<tfstate-storage-account>" \
#          -backend-config="container_name=tfstate" \
#          -backend-config="key=foundation.tfstate" \
#          -backend-config="use_azuread_auth=true"
#   4. Delete the local terraform.tfstate* files after the migration is verified.
#   The operator needs Storage Blob Data Contributor on the container (variable
#   state_operator_object_ids), because shared-key access is disabled.

terraform {
  # 1.11+: write-only arguments and ephemeral resources, used by terraform/environment.
  # Both layers pin the same floor.
  required_version = ">= 1.11.0"

  required_providers {
    # Current majors, checked on the registry 2026-09-24: azurerm 5.6.0 (5.0.0 released
    # 2026-07-28; 4.81.0 was the last 4.x) and azuread 3.9.0. The design's "azurerm ~> 4.0
    # [VERIFY current majors]" is resolved to 5.x. Upgrade guide:
    # https://github.com/hashicorp/terraform-provider-azurerm/blob/main/website/docs/guides/5.0-upgrade-guide.html.markdown
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
