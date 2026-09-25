# Teams and role assignments (ADR-IR34 §7.0, §11.9; ADR-IR32).
#
# ADR-IR32: the only Octopus credential is the Space Manager API key of the existing user AISF-Service-Account. No
# service account, OIDC identity or custom user role (UserEdit and UserRoleEdit are not available); built-in roles only
# (https://octopus.com/docs/security/users-and-teams/default-permissions).
#
# ADR-IR34: the seven live teams and their ten role assignments keep their names and IDs; every scope names
# environments only, never a project or project group, so a new app needs no team change.
#   Platform Engineers    Space Manager. Answers the approvals of env-apply, env-destroy, apps-apply and db-restore.
#   Release Managers      Project Deployer in tdd, uat and prod; Release Creator. Creates Hotfix releases; overrides
#                         the prod freeze with a reason.
#   UAT Approvers         Project Deployer in uat: the UAT sign-off.
#   Prod Approvers        Project Deployer in prod: the prod go/no-go.
#   SRE On-call           Runbook Consumer in uat, prod, infra-nonprod and infra-prod (app runbooks; env-wake and
#                         env-sleep with Sleep.Force); Project Viewer. env-apply and env-destroy still stop at approvals
#                         that only Platform Engineers answer.
#   Developers            Project Viewer.
#   CI Release Publishers Release Creator and Package Publisher. Holds the automation user, which the app release
#                         pipelines use (context platform-octopus).
# Project Deployer includes DeploymentCreate, so the deploying teams can also deploy platform-wake, the first step of
# every app deployment, in their environments (Q31); no separate Deployment Creator grant is needed.
# Risk accepted in ADR-IR32: Project Deployer includes ProjectEdit, so approvers can override the prod freeze.
# Controls: platform-sod-guard, the override reason in the audit log, membership reviews.
#
# The automation user (read by name, never created) is a member of var.automation_user_teams: CI Release Publishers,
# UAT Approvers and Prod Approvers (ADR-IR34: conformance and end-to-end runs answer interventions with a run reason
# while Platform.InterventionTestMode is true), and Platform Engineers, whose approvals the main loop answers at
# provisioning time. Membership grants it nothing beyond its Space Manager role; it only makes it a responsible user.
#
# Team slugs derive from the names (platform-engineers, release-managers, uat-approvers, prod-approvers), which the
# manual interventions of the OCL name as responsible teams.

locals {
  teams = {
    "Platform Engineers"    = "Space managers of the platform: octopus/terraform, sensitive variables, environment and app-layer applies, destroys and restores."
    "Release Managers"      = "Deploy apps to every environment, create Hotfix releases, override the prod weekend freeze with a reason."
    "UAT Approvers"         = "Responsible for the UAT sign-off manual intervention of every app."
    "Prod Approvers"        = "Responsible for the Prod go/no-go manual intervention of every app. Platform.SoDMode decides whether the deployment creator may approve."
    "SRE On-call"           = "Run app runbooks in uat and prod, and env-wake or env-sleep in infra-nonprod and infra-prod; read everything."
    "Developers"            = "View projects, releases, deployments and artifacts."
    "CI Release Publishers" = "Holds the automation user (AISF-Service-Account, ADR-IR32): push packages and build information, create releases."
  }

  builtin_role_names = ["Space Manager", "Project Deployer", "Release Creator", "Package Publisher", "Runbook Consumer", "Project Viewer"]

  # The ten live assignments (ScopedUserRoles-696 to 705), keys unchanged; environments only.
  role_assignments = {
    "platform-engineers-space-manager"        = { team = "Platform Engineers", role = "Space Manager", environments = [] }
    "release-managers-project-deployer"       = { team = "Release Managers", role = "Project Deployer", environments = ["tdd", "uat", "prod"] }
    "release-managers-release-creator"        = { team = "Release Managers", role = "Release Creator", environments = [] }
    "uat-approvers-project-deployer"          = { team = "UAT Approvers", role = "Project Deployer", environments = ["uat"] }
    "prod-approvers-project-deployer"         = { team = "Prod Approvers", role = "Project Deployer", environments = ["prod"] }
    "sre-on-call-runbook-consumer"            = { team = "SRE On-call", role = "Runbook Consumer", environments = ["uat", "prod", "infra-nonprod", "infra-prod"] }
    "sre-on-call-project-viewer"              = { team = "SRE On-call", role = "Project Viewer", environments = [] }
    "developers-project-viewer"               = { team = "Developers", role = "Project Viewer", environments = [] }
    "ci-release-publishers-release-creator"   = { team = "CI Release Publishers", role = "Release Creator", environments = [] }
    "ci-release-publishers-package-publisher" = { team = "CI Release Publishers", role = "Package Publisher", environments = [] }
  }
}

data "octopusdeploy_user_roles" "builtin" {
  for_each = toset(local.builtin_role_names)

  partial_name = each.key
  take         = 20

  lifecycle {
    postcondition {
      condition     = length([for r in self.user_roles : r if lower(r.name) == lower(each.key)]) == 1
      error_message = "Built-in user role '${each.key}' was not found exactly once."
    }
  }
}

# The existing automation user (ADR-IR32): read by name, never created or changed.
data "octopusdeploy_users" "automation" {
  filter = var.automation_username
  take   = 10

  lifecycle {
    postcondition {
      condition     = length([for u in self.users : u if lower(u.username) == lower(var.automation_username)]) == 1
      error_message = "Expected exactly one existing user named '${var.automation_username}'. It is never created here (ADR-IR32)."
    }
  }
}

locals {
  automation_user_id = one([for u in data.octopusdeploy_users.automation.users : u.id if lower(u.username) == lower(var.automation_username)])

  team_members = {
    for name in keys(local.teams) : name => distinct(concat(
      lookup(var.team_member_user_ids, name, []),
      contains(var.automation_user_teams, name) ? [local.automation_user_id] : []
    ))
  }
}

check "automation_user_in_approver_teams" {
  assert {
    condition     = contains(var.automation_user_teams, "UAT Approvers") && contains(var.automation_user_teams, "Prod Approvers")
    error_message = "ADR-IR34: the automation user is a member of UAT Approvers and Prod Approvers (CAP-OCT-003 to CAP-OCT-006)."
  }
}

# Teams are created one at a time: parallel team creation panics in provider 1.20.0, so every plan and apply of this
# configuration uses -parallelism=1 (octopus/apply.ps1).
resource "octopusdeploy_team" "this" {
  for_each = local.teams

  name        = each.key
  description = each.value
  space_id    = var.octopus_space_id
  # Provider 1.20.0 reads an empty member set back as null ("inconsistent result after apply"), so an empty team sends
  # null instead of an empty set.
  users = length(local.team_members[each.key]) > 0 ? toset(local.team_members[each.key]) : null

  dynamic "external_security_group" {
    for_each = lookup(var.team_external_groups, each.key, [])
    content {
      id = external_security_group.value
    }
  }

  lifecycle {
    precondition {
      condition     = length(setsubtract(toset(var.automation_user_teams), toset(keys(local.teams)))) == 0
      error_message = "automation_user_teams names a team that does not exist: ${join(", ", setsubtract(toset(var.automation_user_teams), toset(keys(local.teams))))}."
    }
  }
}

# Updated in place from the preview's project scopes: project_ids and project_group_ids are sent as empty sets.
resource "octopusdeploy_scoped_user_role" "this" {
  for_each = local.role_assignments

  space_id          = var.octopus_space_id
  team_id           = octopusdeploy_team.this[each.value.team].id
  user_role_id      = one([for r in data.octopusdeploy_user_roles.builtin[each.value.role].user_roles : r.id if lower(r.name) == lower(each.value.role)])
  environment_ids   = toset([for e in each.value.environments : octopusdeploy_environment.this[e].id])
  project_ids       = toset([])
  project_group_ids = toset([])
  tenant_ids        = toset([])
}
