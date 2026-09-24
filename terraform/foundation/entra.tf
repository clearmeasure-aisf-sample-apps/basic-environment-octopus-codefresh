# Entra objects (§7.0 "Identities"), created by the provisioner with its Graph permissions:
#   - Application.ReadWrite.OwnedBy: the app registration and service principal sp-platform-conformance.
#     The provisioner manages only what it owns, so it is an owner of both.
#   - Group.Create: group platform-operators. The provisioner owns it and, as owner, adds the members
#     (Q28 [VERIFY], V01). Without User.Read.All it cannot look users up, so members come as object IDs
#     (variable platform_operator_object_ids), and prevent_duplicate_names stays off (it lists groups).
# If the member add is refused, re-apply with platform_operator_object_ids = [] and let an Entra
# administrator add the user; the group's grants do not change.

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

resource "azuread_group" "platform_operators" {
  display_name     = "platform-operators"
  description      = "Platform operators: secret seeding and cluster operation (ADR-IR34, terraform/foundation)."
  security_enabled = true
  owners           = [data.azuread_client_config.current.object_id]
}

resource "azuread_group_member" "platform_operators" {
  for_each = toset(var.platform_operator_object_ids)

  group_object_id  = azuread_group.platform_operators.object_id
  member_object_id = each.key
}
