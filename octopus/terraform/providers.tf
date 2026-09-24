# Provider authentication: the Space Manager API key of the automation user (ADR-IR32), from TF_VAR_octopus_api_key
# or, when that is unset, from the environment variable OCTOPUS_APIKEY, which the provider reads itself. Never in files.
# An OIDC access token (TF_VAR_octopus_access_token) also works; the provider prefers the API key when both are set.
#
# Required Octopus permissions: Space Manager in <octopus-space> covers every object of this configuration, including
# teams (TeamCreate, TeamEdit), assignments of built-in roles, step templates, triggers and sensitive variables. Nothing
# needs System Manager: no user, service account, OIDC identity or custom role is created (ADR-IR32).
# Known provider 1.20.0 limits, handled here and in octopus/apply.sh: sort_order 0 counts as unset
# (environments.tf), an empty team member set reads back as null (teams.tf), and concurrent team creates made
# Terraform panic, so every apply runs with -parallelism=1.

provider "octopusdeploy" {
  address      = var.octopus_url
  space_id     = var.octopus_space_id
  access_token = var.octopus_access_token
  api_key      = var.octopus_api_key
}

# Azure, read-only (azure.tf): client IDs of the identities behind the OIDC accounts and feed acr-apps. The session
# that applies terraform/foundation authenticates it (ARM_* environment variables or az login); it needs Reader on the
# subscription, and this configuration never writes to Azure.
provider "azurerm" {
  subscription_id = var.azure_subscription_id
  tenant_id       = var.azure_tenant_id

  # Registration belongs to terraform/foundation; nothing here needs it.
  resource_provider_registrations = "none"

  features {}
}
