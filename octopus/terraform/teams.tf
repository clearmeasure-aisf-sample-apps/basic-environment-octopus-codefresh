# Teams, the custom user role, service accounts and the Codefresh OIDC identity (§7.2, §5.2, ADR-D8, ADR-D13).
#
# Permission names are those of https://octopus.com/docs/security/users-and-teams/default-permissions.
# Built-in roles are looked up by name (case-insensitive). Assignments:
#   Platform Engineers    Space Manager (space-wide; §5.2). Approve env-apply, env-destroy and restore swaps.
#   Release Managers      Project Deployer + Release Creator on workorders. Create Hotfix releases; override the
#                         prod freeze with a reason (ProjectEdit scoped to prod, per
#                         https://octopus.com/docs/deployments/deployment-freezes/project-deployment-freezes).
#   UAT Approvers         Work Orders Approver on workorders, uat (custom role, ADR-IR17).
#   Prod Approvers        Work Orders Approver on workorders, prod (custom role, ADR-IR17).
#   SRE On-call           Runbook Consumer on workorders (uat, prod) + Project Viewer on the group.
#   Developers            Project Viewer on the group (view only).
#   CI Release Publishers CI Release Publisher on workorders; holds svc-codefresh-release.
# Manual interventions need InterruptionViewSubmitResponsible. Among built-in roles it comes only with roles that
# also grant ProjectEdit (Project Contributor, Deployer, Lead), which would let approvers override the prod freeze.
# The custom role Work Orders Approver grants the intervention permissions without ProjectEdit and without
# DeploymentCreate: approvers answer interventions; Release Managers create UAT and Prod deployments and override the
# prod freeze with a reason (Space Managers can too) (ADR-IR17). Permission names match the live platform-space
# permission set.
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
    "CI Release Publishers" = "Holds svc-codefresh-release: push packages and build information, create releases on channel Default."
  }

  builtin_role_names = ["Space Manager", "Project Deployer", "Release Creator", "Runbook Consumer", "Project Viewer", "Deployment Creator"]

  project_ids = {
    "workorders"                = octopusdeploy_project.workorders.id
    "workorders-infrastructure" = octopusdeploy_project.workorders_infrastructure.id
  }

  role_assignments = merge(
    {
      "platform-engineers-space-manager"   = { team = "Platform Engineers", role = "Space Manager", projects = [], project_groups = false, environments = [] }
      "release-managers-project-deployer"  = { team = "Release Managers", role = "Project Deployer", projects = ["workorders"], project_groups = false, environments = [] }
      "release-managers-release-creator"   = { team = "Release Managers", role = "Release Creator", projects = ["workorders"], project_groups = false, environments = [] }
      "uat-approvers-approver"             = { team = "UAT Approvers", role = "Work Orders Approver", projects = ["workorders"], project_groups = false, environments = ["uat"] }
      "prod-approvers-approver"            = { team = "Prod Approvers", role = "Work Orders Approver", projects = ["workorders"], project_groups = false, environments = ["prod"] }
      "sre-on-call-runbook-consumer"       = { team = "SRE On-call", role = "Runbook Consumer", projects = ["workorders"], project_groups = false, environments = ["uat", "prod"] }
      "sre-on-call-project-viewer"         = { team = "SRE On-call", role = "Project Viewer", projects = [], project_groups = true, environments = [] }
      "developers-project-viewer"          = { team = "Developers", role = "Project Viewer", projects = [], project_groups = true, environments = [] }
      "ci-release-publishers-ci-publisher" = { team = "CI Release Publishers", role = "CI Release Publisher", projects = ["workorders"], project_groups = false, environments = [] }
    },
    # Q3: only if lifecycle auto-deploy to TDD needs DeploymentCreate for the release creator.
    var.ci_release_publisher_tdd_deploy ? {
      "ci-release-publishers-tdd-deployment-creator" = { team = "CI Release Publishers", role = "Deployment Creator", projects = ["workorders"], project_groups = false, environments = ["tdd"] }
    } : {}
  )

  # §7.2 pattern: account:<CF_ACCOUNT_ID>:pipeline:<CF_RELEASE_PIPELINE_ID>:*:scm_ref:master. Codefresh push tokens
  # embed scm_user_name in sub (E15); copy the exact sub from a test build and wildcard only the user segment (E19).
  codefresh_oidc_subject = coalesce(
    var.codefresh_oidc_subject,
    "account:${var.codefresh_account_id}:pipeline:${var.codefresh_release_pipeline_id}:*:scm_ref:master"
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

# Custom role for the CI release creator (§5.2, TB5): no deployment, no variable or process edits.
# The permission list is the §7.2 contract. If release creation on the version-controlled project also needs
# ProcessView, LifecycleView or EnvironmentView, the first test release shows it [VERIFY].
resource "octopusdeploy_user_role" "ci_release_publisher" {
  name        = "CI Release Publisher"
  description = "Codefresh workorders/release via svc-codefresh-release: push to the built-in feed, push build information, create and view releases, view the project and feeds."
  granted_space_permissions = [
    "BuiltInFeedPush",
    "BuildInformationPush",
    "ReleaseCreate",
    "ReleaseView",
    "ProjectView",
    "FeedView",
  ]
}

# Custom role for the UAT and Prod approver teams (ADR-IR17): view the release and the deployment, answer the
# manual interventions they are responsible for. No ProjectEdit (no freeze override), no DeploymentCreate.
resource "octopusdeploy_user_role" "workorders_approver" {
  name        = "Work Orders Approver"
  description = "UAT and Prod approvers: view releases and deployments, take and submit their manual interventions. No project edit, no deployment creation."
  granted_space_permissions = [
    "ProjectView",
    "ProcessView",
    "ReleaseView",
    "DeploymentView",
    "EnvironmentView",
    "LifecycleView",
    "TaskView",
    "ArtifactView",
    "EventView",
    "InterruptionView",
    "InterruptionSubmit",
    "InterruptionViewSubmitResponsible",
  ]
}

locals {
  custom_role_ids = {
    "CI Release Publisher" = octopusdeploy_user_role.ci_release_publisher.id
    "Work Orders Approver" = octopusdeploy_user_role.workorders_approver.id
  }
}

resource "octopusdeploy_team" "this" {
  for_each = local.teams

  name        = each.key
  description = each.value
  space_id    = var.octopus_space_id
  users = toset(concat(
    lookup(var.team_member_user_ids, each.key, []),
    each.key == "CI Release Publishers" ? [octopusdeploy_user.svc_codefresh_release.id] : []
  ))

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
  user_role_id      = contains(keys(local.custom_role_ids), each.value.role) ? local.custom_role_ids[each.value.role] : one([for r in data.octopusdeploy_user_roles.builtin[each.value.role].user_roles : r.id if lower(r.name) == lower(each.value.role)])
  project_ids       = toset([for p in each.value.projects : local.project_ids[p]])
  project_group_ids = each.value.project_groups ? toset([octopusdeploy_project_group.work_orders.id]) : toset([])
  environment_ids   = toset([for e in each.value.environments : octopusdeploy_environment.this[e].id])
}

# Service accounts (system-level users; creating them needs UserEdit, see providers.tf).
resource "octopusdeploy_user" "svc_codefresh_release" {
  username     = "svc-codefresh-release"
  display_name = "svc-codefresh-release"
  is_service   = true
  is_active    = true
}

# Registers the Argo CD instances argocd-nonprod (tdd, uat) and argocd-prod (prod) through the gateway. Its access
# token is stored in <kv-workorders-platform-{cluster}> as octopus-gateway-registration-token (§7.8). The required
# permission is [VERIFY] (§5.2), so no role is granted here yet; grant the narrowest role the spike proves.
resource "octopusdeploy_user" "svc_argocd_gateway" {
  username     = "svc-argocd-gateway"
  display_name = "svc-argocd-gateway"
  is_service   = true
  is_active    = true
}

# Codefresh workorders/release on master exchanges its OIDC token for an Octopus access token (E16, E17).
# No audience is set: Codefresh sends AUDIENCE = the service account ID (§7.7), the default audience of an Octopus
# OIDC identity [VERIFY].
resource "octopusdeploy_service_account_oidc_identity" "codefresh_release_master" {
  name               = "codefresh-release-master"
  service_account_id = octopusdeploy_user.svc_codefresh_release.id
  issuer             = "https://oidc.codefresh.io"
  subject            = local.codefresh_oidc_subject
}
