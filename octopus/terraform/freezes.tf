# Deployment freeze prod-weekend-freeze (ADR-IR34 §7.0, ADR-D5): prod, every weekend, every app project that deploys
# to prod (for_each over apps/*.yaml).
#
# Project freezes, one per app project: multi-project freezes need an Enterprise licence, project freezes are
# available to every customer and support recurring schedules
# (https://octopus.com/docs/deployments/deployment-freezes/project-deployment-freezes). The live freeze of workorders
# (DeploymentFreezes-1) is kept (moved.tf). Each freeze is named prod-weekend-freeze-<project>: whether freeze names
# must be unique across the space or the instance is [UNVERIFIED], and distinct names cannot collide. The contract
# name prod-weekend-freeze stays the prefix, so the calendar reads the same in every project.
# Override: ProjectEdit scoped to prod (Release Managers; the approvers too, accepted risk of ADR-IR32), with a reason
# in the override dialog. Freezes also block automatic lifecycle promotions, which never target prod here.
# Recurrence: the first window (Saturday 00:00 to Monday 00:00, UTC) repeats weekly on Saturday [VERIFY that each
# occurrence keeps the 48-hour length].

resource "octopusdeploy_project_deployment_freeze" "prod_weekend" {
  for_each = { for project, p in local.app_projects : project => p if contains(p.environments, "prod") }

  name            = "prod-weekend-freeze-${each.key}"
  owner_id        = octopusdeploy_project.app[each.key].id
  environment_ids = [octopusdeploy_environment.this["prod"].id]
  start           = var.prod_freeze_first_window.start
  end             = var.prod_freeze_first_window.end

  recurring_schedule = {
    type         = "Weekly"
    unit         = 1
    days_of_week = ["Saturday"]
    end_type     = "Never"
  }
}
