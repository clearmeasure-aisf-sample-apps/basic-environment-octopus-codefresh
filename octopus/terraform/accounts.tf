# Azure OIDC accounts (ADR-IR34 §7.0, decision 3). Execution subject keys space, project and environment give
#   space:<octopus-space-slug>:project:platform-infrastructure:environment:infra-<tier>   azure-platform-lifecycle-<tier>
#   space:<octopus-space-slug>:project:<project>:environment:<env>                        azure-<app>-<env>
# (https://octopus.com/docs/infrastructure/accounts/openid-connect). terraform/foundation (lifecycle identities) and
# terraform/apps/grants (deploy identities) create the identities and the matching federated credentials with issuer
# <OCTOPUS_URL> (no trailing slash) and audience api://AzureADTokenExchange; azure.tf reads their client IDs and
# outputs.tf renders the subjects for cross-checking. Health
# and account-test subjects keep their defaults and have no federated credential, so "Save and test" fails by design.
# Account names are also their slugs, which the OCL references (Azure.LifecycleAccount, Azure.DeployAccount).
#
# Stored `Azure Runtime Provisioner` is looked up only; no project uses it from P1 on (decision 3).

resource "octopusdeploy_azure_openid_connect" "platform_lifecycle" {
  for_each = local.tier_environments

  name                              = "azure-platform-lifecycle-${each.key}"
  description                       = "id-platform-lifecycle-${each.key}: tier automation of ${each.key} (terraform/tier, terraform/apps/tier, env-wake, env-sleep, rotate-db-passwords). Project platform-infrastructure, environment ${each.value} only."
  application_id                    = data.azurerm_user_assigned_identity.lifecycle[each.key].client_id
  tenant_id                         = var.azure_tenant_id
  subscription_id                   = var.azure_subscription_id
  audience                          = "api://AzureADTokenExchange"
  execution_subject_keys            = ["space", "project", "environment"]
  environments                      = [octopusdeploy_environment.this[each.value].id]
  tenanted_deployment_participation = "Untenanted"
}

# Optional per-app accounts (descriptor octopus.azureAccount: true), one per app environment, restricted to it.
# Until terraform/apps/grants (P1-09) has created id-<app>-<env>-deploy, the account carries a placeholder client ID
# so that the app's OCL, which names it, loads; the re-apply after P1-09 sets the real ID in place (azure.tf).
resource "octopusdeploy_azure_openid_connect" "app" {
  for_each = local.app_accounts

  name                              = "azure-${each.key}"
  description                       = "id-${each.key}-deploy of app ${each.value.app} (terraform/apps/grants): Key Vault Secrets User on its vault, Contributor on rg-app-${each.value.app}-${local.environment_tiers[each.value.environment]} when declared. Environment ${each.value.environment} only."
  application_id                    = lookup(local.app_deploy_client_ids, each.key, local.placeholder_client_id)
  tenant_id                         = var.azure_tenant_id
  subscription_id                   = var.azure_subscription_id
  audience                          = "api://AzureADTokenExchange"
  execution_subject_keys            = ["space", "project", "environment"]
  environments                      = [octopusdeploy_environment.this[each.value.environment].id]
  tenanted_deployment_participation = "Untenanted"
}

data "octopusdeploy_accounts" "stored_provisioner" {
  account_type = "AzureServicePrincipal"
  partial_name = var.stored_azure_account_name
  take         = 10
}

locals {
  stored_provisioner_account = one([for a in data.octopusdeploy_accounts.stored_provisioner.accounts : a if a.name == var.stored_azure_account_name])
}
