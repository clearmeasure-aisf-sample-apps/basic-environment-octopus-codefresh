# Observability of the tier (ADR-D15 as amended by ADR-IR34): the workspace log-platform-<tier>, the action group
# ag-platform-oncall and the sleep suppression rule apr-sleep-<tier>. Per-app App Insights resources and SLO
# alerts (appi-<app>-<env>, slo-fast-burn-<app>-<env>) belong to terraform/apps/tier; they live in
# rg-platform-<tier>-apps, send to this workspace and notify this action group.

resource "azurerm_log_analytics_workspace" "this" {
  name                = "log-platform-${var.tier}"
  location            = var.location
  resource_group_name = local.rg_shared
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days

  # Resource-context access: a reader of one App Insights resource sees only its own data.
  allow_resource_only_permissions = true

  tags = merge(local.base_tags, { "platform-component" = "logs" })

  # Kept through a rebuild (versions.tf, "Rebuild"): audit logs and every appi-<app>-<env> live here.
  lifecycle {
    prevent_destroy = true
  }
}

resource "azurerm_monitor_action_group" "oncall" {
  name                = "ag-platform-oncall"
  resource_group_name = local.rg_aks
  short_name          = var.tier == "prod" ? "oncall-prod" : "oncall-np"

  dynamic "email_receiver" {
    for_each = var.oncall_email_receivers

    content {
      name                    = email_receiver.key
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }

  tags = merge(local.base_tags, { "platform-component" = "alerts" })
}

# --- Sleep suppression (ADR-IR33, docs/runbooks/sleep-and-wake.md) -----------------------------------------------
# While the tier's cluster sleeps, nothing in it should page anyone. Runbook env-sleep (platform-infrastructure)
# enables this rule just before it stops the cluster; env-wake disables it after the start. Terraform creates the
# rule disabled and never touches `enabled` afterwards, so an apply (which always runs after env-wake) cannot undo
# either runbook. CAP-AZ-004 tests both states.
#
# Scope: the tier's cluster group and its apps group, where the per-app App Insights resources and SLO alerts
# live; a new app needs no change. Condition: every monitor service except the activity log, so security alerts
# on the activity log are never suppressed. An enabled rule stops notifications only; alerts still fire and stay
# in the alert history.
locals {
  sleep_resource_groups = [local.rg_aks, local.rg_apps]
}

resource "azurerm_monitor_alert_processing_rule_suppression" "sleep" {
  name                = "apr-sleep-${var.tier}"
  resource_group_name = local.rg_aks
  description         = "Suppresses metric and log alert notifications of the ${var.tier} tier while ${local.cluster_name} sleeps. Toggled only by the Octopus runbooks env-sleep and env-wake (docs/runbooks/sleep-and-wake.md)."
  scopes              = [for rg in local.sleep_resource_groups : "/subscriptions/${var.subscription_id}/resourceGroups/${rg}"]
  enabled             = false

  condition {
    monitor_service {
      operator = "NotEquals"
      values = [
        "ActivityLog Administrative",
        "ActivityLog Autoscale",
        "ActivityLog Policy",
        "ActivityLog Recommendation",
        "ActivityLog Security",
      ]
    }
  }

  tags = merge(local.base_tags, { "platform-component" = "alerts" })

  lifecycle {
    # env-sleep and env-wake own this flag.
    ignore_changes = [enabled]
  }
}
