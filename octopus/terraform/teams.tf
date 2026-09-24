# Teams and role assignments (§7.2, §5.2, ADR-D13, ADR-IR32).
#
# ADR-IR32 (user directive): the only Octopus credential is the Space Manager API key of the existing user
# AISF-Service-Account, in the prototype space only. No service accounts, no OIDC identity, no custom user roles
# (UserEdit and UserRoleEdit are not available); Codefresh and the Argo CD gateway both use that key. Teams are
# space teams and use BUILT-IN roles only (https://octopus.com/docs/security/users-and-teams/default-permissions):
#   Platform Engineers    Space Manager (space-wide; §5.2). Approve env-apply, env-destroy and restore swaps.
#   Release Managers      Project Deployer + Release Creator on workorders. Create Hotfix releases; override the
#                         prod freeze with a reason (ProjectEdit scoped to prod).
#   UAT Approvers         Project Deployer on workorders, uat.
#   Prod Approvers        Project Deployer on workorders, prod.
#   SRE On-call           Runbook Consumer on workorders (uat, prod) + Project Viewer on the group.
#   Developers            Project Viewer on the group (view only).
#   CI Release Publishers Release Creator + Package Publisher on workorders; holds AISF-Service-Account, which
#                         already has Space Manager, so the team documents the intended narrow grant (the path back
#                         to a dedicated account, ADR-IR32).
# Risk accepted in ADR-IR32: Project Deployer includes ProjectEdit and DeploymentCreate, so approvers can create
# deployments in their environment and override the prod freeze. Mitigations: sod-guard (the Prod go/no-go
# approver must differ from the deployment creator, and neither may be the automation user), the freeze override
# reason in the audit log, and team membership reviews.
#
# Space teams: slugs derive from the names (release-managers, prod-approvers, uat-approvers, platform-engineers),
# which the OCL manual interventions reference.

locals {
  teams = {
    "Platform Engineers"    = "Space managers of the Work Orders platform: octopus/terraform, sensitive variables, environment applies and destroys."
    "Release Managers"      = "Deploy workorders to every environment, create Hotfix releases, override the prod weekend freeze with a reason."
    "UAT Approvers"         = "Responsible for the UAT sign-off manual intervention."
    "Prod Approvers"        = "Responsible for the Prod go/no-go manual intervention. The approver must not create the deployment."
    "SRE On-call"           = "Run db-backup and db-restore-pitr in uat and prod; read everything in Work Orders."
    "Developers"            = "View Work Orders projects, releases, deployments and artifacts."
    "CI Release Publishers" = "Holds the automation user (AISF-Service-Account, ADR-IR32): push packages and build information, create releases on channel Default."
  }

  builtin_role_names = ["Space Manager", "Project Deployer", "Release Creator", "Package Publisher", "Runbook Consumer", "Project Viewer", "Deployment Creator"]

  project_ids = {
    "workorders"                = octopusdeploy_project.workorders.id
    "workorders-infrastructure" = octopusdeploy_project.workorders_infrastructure.id
  }

  role_assignments = merge(
    {
      "platform-engineers-space-manager"        = { team = "Platform Engineers", role = "Space Manager", projects = [], project_groups = false, environments = [] }
      "release-managers-project-deployer"       = { team = "Release Managers", role = "Project Deployer", projects = ["workorders"], project_groups = false, environments = [] }
      "release-managers-release-creator"        = { team = "Release Managers", role = "Release Creator", projects = ["workorders"], project_groups = false, environments = [] }
      "uat-approvers-project-deployer"          = { team = "UAT Approvers", role = "Project Deployer", projects = ["workorders"], project_groups = false, environments = ["uat"] }
      "prod-approvers-project-deployer"         = { team = "Prod Approvers", role = "Project Deployer", projects = ["workorders"], project_groups = false, environments = ["prod"] }
      "sre-on-call-runbook-consumer"            = { team = "SRE On-call", role = "Runbook Consumer", projects = ["workorders"], project_groups = false, environments = ["uat", "prod"] }
      "sre-on-call-project-viewer"              = { team = "SRE On-call", role = "Project Viewer", projects = [], project_groups = true, environments = [] }
      "developers-project-viewer"               = { team = "Developers", role = "Project Viewer", projects = [], project_groups = true, environments = [] }
      "ci-release-publishers-release-creator"   = { team = "CI Release Publishers", role = "Release Creator", projects = ["workorders"], project_groups = false, environments = [] }
      "ci-release-publishers-package-publisher" = { team = "CI Release Publishers", role = "Package Publisher", projects = [], project_groups = false, environments = [] }
    },
    # Q3: only if lifecycle auto-deploy to TDD needs DeploymentCreate for the release creator.
    var.ci_release_publisher_tdd_deploy ? {
      "ci-release-publishers-tdd-deployment-creator" = { team = "CI Release Publishers", role = "Deployment Creator", projects = ["workorders"], project_groups = false, environments = ["tdd"] }
    } : {}
  )
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

locals {
  team_members = {
    for name in keys(local.teams) : name => concat(
      lookup(var.team_member_user_ids, name, []),
      name == "CI Release Publishers" ? [local.automation_user_id] : []
    )
  }
}

resource "octopusdeploy_team" "this" {
  for_each = local.teams

  name        = each.key
  description = each.value
  space_id    = var.octopus_space_id
  # Provider 1.20.0 reads an empty member set back as null ("inconsistent result after apply"), so an empty
  # team sends null instead of an empty set.
  users = length(local.team_members[each.key]) > 0 ? toset(local.team_members[each.key]) : null

  dynamic "external_security_group" {
    for_each = lookup(var.team_external_groups, each.key, [])
    content {
      id = external_security_group.value
    }
  }
}

resource "octopusdeploy_scoped_user_role" "this" {
  for_each = local.role_assignments

  space_id          = var.octopus_space_id
  team_id           = octopusdeploy_team.this[each.value.team].id
  user_role_id      = one([for r in data.octopusdeploy_user_roles.builtin[each.value.role].user_roles : r.id if lower(r.name) == lower(each.value.role)])
  project_ids       = toset([for p in each.value.projects : local.project_ids[p]])
  project_group_ids = each.value.project_groups ? toset([octopusdeploy_project_group.work_orders.id]) : toset([])
  environment_ids   = toset([for e in each.value.environments : octopusdeploy_environment.this[e].id])
}

# The existing automation user (ADR-IR32): read by name, never created or changed. Its API key is the Codefresh
# secret context workorders-octopus (OCTOPUS_API_KEY) and, from phase 1, the gateway registration token.
data "octopusdeploy_users" "automation" {
  filter = var.automation_username
  take   = 10

  lifecycle {
    postcondition {
      condition     = length([for u in self.users : u if u.username == var.automation_username]) == 1
      error_message = "Expected exactly one existing user named '${var.automation_username}'. It is never created here (ADR-IR32)."
    }
  }
}

locals {
  automation_user_id = one([for u in data.octopusdeploy_users.automation.users : u.id if u.username == var.automation_username])
}
