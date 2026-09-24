# Per-app observability (ADR-D15 as amended by ADR-IR34): appi-<app>-<env>, workspace-based in log-platform-<tier>,
# and the fast-burn SLO alert slo-fast-burn-<app>-<env> to ag-platform-oncall of the tier. Both live in
# rg-platform-<tier>-apps, which apr-sleep-<tier> covers, so a sleeping tier pages nobody (ADR-IR33).
#
# The connection string reaches the app only through its vault (key appinsights-connection-string, synced by ESO).
# It is not a password; it stays in state like the resource that produces it.

resource "azurerm_application_insights" "app" {
  for_each = local.envs

  name                = "appi-${var.app}-${each.key}"
  location            = var.location
  resource_group_name = local.rg_apps
  workspace_id        = data.azurerm_log_analytics_workspace.tier.id
  application_type    = "web"

  # Apps authenticate telemetry with the connection string; Entra-only ingestion would need a credential in every
  # app, so local authentication stays on.
  local_authentication_enabled = true
  ip_masking_enabled           = true

  tags = merge(local.env_tags[each.key], { "platform-component" = "app-insights" })
}

resource "azurerm_key_vault_secret" "appinsights" {
  for_each = local.envs

  name         = "appinsights-connection-string"
  key_vault_id = azurerm_key_vault.app[each.key].id
  value        = azurerm_application_insights.app[each.key].connection_string
  content_type = "App Insights connection string of appi-${var.app}-${each.key}"
}

# --- Fast-burn SLO alert (ADR-D15) ------------------------------------------------------------------------------------
# SLO: 99.5 % of the app's requests complete without a 5xx over 28 days (error budget 0.5 %). Fast burn: 2 % of the
# 28-day budget within one hour, a burn rate of 0.02 * 672 = 13.44, confirmed over 5 minutes so the alert also
# clears quickly. Common probe and version paths are excluded, and at least 50 requests an hour keep one failed
# request on an idle environment from paging. An app that sends no telemetry never fires it.
# Response: docs/runbooks/slo-fast-burn.md.

locals {
  slo_fast_burn_query = <<-KQL
    let slo = 0.995;
    let burnThreshold = 13.44;
    let minRequests = 50;
    let excludedPaths = dynamic(["/alive", "/health", "/healthz", "/ready", "/readyz", "/livez", "/_healthcheck", "/_healthcheck/detailed", "/_version", "/version"]);
    let scoped = requests
        | where timestamp > ago(1h)
        | extend path = tostring(parse_url(url).Path)
        | where path !in (excludedPaths);
    let longWindow = scoped
        | summarize total = count(), failed = countif(toint(resultCode) >= 500)
        | extend window = "1h";
    let shortWindow = scoped
        | where timestamp > ago(5m)
        | summarize total = count(), failed = countif(toint(resultCode) >= 500)
        | extend window = "5m";
    union longWindow, shortWindow
    | extend burnRate = iff(total == 0, 0.0, (todouble(failed) / todouble(total)) / (1.0 - slo))
    | summarize longBurn = maxif(burnRate, window == "1h"), shortBurn = maxif(burnRate, window == "5m"), requestsLastHour = maxif(total, window == "1h")
    | where requestsLastHour >= minRequests and longBurn >= burnThreshold and shortBurn >= burnThreshold
  KQL
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "slo_fast_burn" {
  for_each = local.envs

  name                 = "slo-fast-burn-${var.app}-${each.key}"
  display_name         = "${var.app} ${each.key}: error budget fast burn"
  description          = "More than 2 % of the 28-day error budget (99.5 % non-5xx) burned in the last hour, confirmed over 5 minutes. Runbook: docs/runbooks/slo-fast-burn.md."
  resource_group_name  = local.rg_apps
  location             = var.location
  scopes               = [azurerm_application_insights.app[each.key].id]
  enabled              = var.slo_alerts_enabled
  severity             = each.key == "prod" ? 1 : 3
  evaluation_frequency = "PT5M"
  window_duration      = "PT1H"

  criteria {
    query                   = local.slo_fast_burn_query
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0

    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  auto_mitigation_enabled = true

  action {
    action_groups = [data.azurerm_monitor_action_group.oncall.id]
    custom_properties = {
      app         = var.app
      environment = each.key
      runbook     = "docs/runbooks/slo-fast-burn.md"
    }
  }

  tags = merge(local.env_tags[each.key], { "platform-component" = "app-alerts" })
}
