# Hourly env-sleep triggers (ADR-IR33, §7.0): env-sleep-hourly-nonprod (infra-nonprod) and env-sleep-hourly-prod
# (infra-prod) run runbook env-sleep of platform-infrastructure at minute 0 of every hour (Octopus cron has a seconds
# field: "0 0 * * * *"). env-sleep decides from the working window, idle time and running tasks, and exits within
# seconds when the cluster stays up, so each firing holds one of the 5 task slots only briefly.
# A trigger runs a runbook stored in Git from the latest commit of the default branch
# (https://octopus.com/docs/runbooks/config-as-code-runbooks, "Scheduled triggers").
#
# runbook_id: the provider sends it as-is. For a runbook stored in Git the ID is its slug env-sleep [VERIFY, Q27;
# check V06 of docs/preview-octopus.md]. If Octopus rejects it, apply with env_sleep_triggers_managed = false and
# create both triggers through the REST API as docs/preview-octopus.md describes.

locals {
  env_sleep_triggers = var.env_sleep_triggers_managed ? {
    for tier, environment in local.tier_environments : "env-sleep-hourly-${tier}" => environment
  } : {}
}

resource "octopusdeploy_project_scheduled_trigger" "env_sleep" {
  for_each = local.env_sleep_triggers

  name        = each.key
  description = "Runs env-sleep in ${each.value} every hour (ADR-IR33): the cluster sleeps outside the working window, after the idle time, when no task runs."
  space_id    = var.octopus_space_id
  project_id  = octopusdeploy_project.platform_infrastructure.id
  timezone    = var.env_sleep_trigger_timezone
  is_disabled = false

  run_runbook_action {
    runbook_id             = "env-sleep"
    target_environment_ids = [octopusdeploy_environment.this[each.value].id]
  }

  cron_expression_schedule {
    cron_expression = "0 0 * * * *"
  }
}
