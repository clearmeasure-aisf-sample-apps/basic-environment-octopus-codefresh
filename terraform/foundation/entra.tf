# Entra objects (§7.0 "Identities"), created by the provisioner with its Graph permissions:
#   - Application.ReadWrite.OwnedBy: the app registration and service principal sp-platform-conformance.
#     The provisioner manages only what it owns, so it is an owner of both.
#   - Group.Create: group platform-operators, created with az outside Terraform (see below): the
#     provider refreshes group owners, which Group.Create does not allow reading.

data "azuread_client_config" "current" {}

# --- sp-platform-conformance ----------------------------------------------------------------------------
# The .NET conformance harness (tests/Platform.Conformance.sln). No password block: the 90-day client
# secret is created by CLI at P1-05 and kept only in Codefresh context platform-conformance
# (AZURE_CLIENT_SECRET), so it never reaches state:
#   az ad app credential reset --id <client-id> --append --display-name platform-conformance \
#     --end-date <today + 90 days> --query password --output tsv
# A secret added outside Terraform does not show as drift: the resource tracks only a password it
# created itself.

resource "azuread_application" "conformance" {
  display_name     = "sp-platform-conformance"
  description      = "Platform conformance harness: reads the platform resource groups and writes only in the sandbox namespaces and the sandbox tdd vault (ADR-IR34)."
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.current.object_id]
}

resource "azuread_service_principal" "conformance" {
  client_id = azuread_application.conformance.client_id
  owners    = [data.azuread_client_config.current.object_id]
}

# --- platform-operators ---------------------------------------------------------------------------------
# The user: secret seeding and cluster operation (replaces secret-writers, ADR-IR34 decision 25).
# Grants in role-assignments.tf: Key Vault Secrets Officer on rg-platform-<tier>-aks and -apps, and AKS
# RBAC Cluster Admin on the three cluster resource groups.

# Group platform-operators (the user; ADR-IR34 decision 25) is NOT managed here. The provisioner holds
# Graph Group.Create but no read permission on groups, and the azuread provider reads every group's
# owners on refresh ("Could not retrieve owners for Group", seen on the first live apply, 2026-09-24).
# The provisioner creates the group once and adds the members as its owner (docs/bootstrap.md P1-02):
#   az ad group create --display-name platform-operators --mail-nickname platform-operators \
#     --description "Platform operators (ADR-IR34)" --query id --output tsv
#   az ad group member add --group <group-object-id> --member-id <user-object-id>
# and passes the object ID in var.platform_operators_group_object_id.
