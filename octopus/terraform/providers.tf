# Provider authentication: an OIDC access token or an API key, never both (the provider prefers the API key when
# both are set). Pass them as TF_VAR_octopus_access_token or TF_VAR_octopus_api_key in the shell; never in files.
#
# Required Octopus permissions [from https://octopus.com/docs/security/users-and-teams/default-permissions]:
# - Space Manager in <octopus-space> covers environments, lifecycles, projects, channels, feeds, accounts, worker
#   pools, library variable sets, teams, triggers and project freezes.
# - Service accounts (UserEdit) and the custom roles `CI Release Publisher` and `Work Orders Approver`
#   (UserRoleEdit) are System Manager permissions. §5.2 keeps the platform engineers at Space Manager
#   (ADR-IR16): a System Manager runs the first apply, which creates them. Later applies by a Space Manager only
#   read them (UserView and UserRoleView are granted to space managers, as the live permission set of the
#   platform space shows); a change to one of them needs the System Manager again.

provider "octopusdeploy" {
  address      = var.octopus_url
  space_id     = var.octopus_space_id
  access_token = var.octopus_access_token
  api_key      = var.octopus_api_key
}
