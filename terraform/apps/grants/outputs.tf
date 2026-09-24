# Outputs of one app's grants. None is secret. octopus/terraform reads the deploy identities by name (azurerm,
# read-only) to create the OIDC accounts azure-<app>-<env>; the outputs repeat them for operators.

output "resource_groups" {
  description = "rg-app-<app>-<tier> per tier; empty unless azure.resourceGroup."
  value       = { for t, rg in azurerm_resource_group.app : t => rg.name }
}

output "deploy_identities" {
  description = "id-<app>-<env>-deploy per environment: name, client ID, principal ID and the Octopus account that uses it."
  value = {
    for e, i in azurerm_user_assigned_identity.deploy : e => {
      name            = i.name
      client_id       = i.client_id
      principal_id    = i.principal_id
      octopus_account = "azure-${var.app}-${e}"
    }
  }
}

output "app_identities" {
  description = "id-<app>-<env>-app per environment: name, client ID and principal ID. apps-apply writes the client ID to the vault."
  value = {
    for e, i in azurerm_user_assigned_identity.app : e => {
      name         = i.name
      client_id    = i.client_id
      principal_id = i.principal_id
    }
  }
}

output "deploy_subjects" {
  description = "Octopus federated subjects per identity and project."
  value       = { for k, c in azurerm_federated_identity_credential.deploy : k => c.subject }
}
