# Provider authentication: an OIDC access token or an API key, never both (the provider prefers the API key when
# both are set). Pass them as TF_VAR_octopus_access_token or TF_VAR_octopus_api_key in the shell; never in files.
#
# Required Octopus permissions [from https://octopus.com/docs/security/users-and-teams/default-permissions]:
# - Space Manager in <octopus-space> covers environments, lifecycles, projects, channels, feeds, accounts, worker
#   pools, machine policies, library variable sets and their sensitive values, teams, triggers and project freezes.
# - Nothing here needs System Manager (ADR-IR32): no service accounts, no OIDC identity, no custom user roles.
#   Teams (TeamCreate, TeamEdit) and scoped assignments of built-in roles are within Space Manager rights, and the
#   existing user AISF-Service-Account is only read (UserView).

provider "octopusdeploy" {
  address      = var.octopus_url
  space_id     = var.octopus_space_id
  access_token = var.octopus_access_token
  api_key      = var.octopus_api_key
}
