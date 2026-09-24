# Three monthly budgets (§7.0 "Budgets", §3.5, R18): budget-platform-build (with rg-platform-global),
# budget-platform-nonprod and budget-platform-prod. Each is a subscription budget filtered by
# resource-group name, so the AKS node groups count although AKS creates them later [VERIFY that the
# Budgets API accepts a group name before the group exists]. Notifications go to the subscription's
# Owners and to budget_contact_emails; nothing stops or scales resources.
#
# Cost Management does not support some offers, among them Microsoft Azure Sponsorship (quota ID
# Sponsored_2016-01-01; learn.microsoft.com "Understand Cost Management data", checked 2026-09-24). The
# live subscription is sponsored, so with budgets_enabled = null the budgets are skipped there and
# output budgets.status says why; CAP-AZ-014 then reports the gap.

locals {
  cost_management_unsupported_quota_ids = [
    "Sponsored_2016-01-01",
    "AzureForStudents_2018-01-01",
    "DreamSpark_2015-02-01",
    "Default_2014-09-01",
  ]

  budgets_supported = !contains(local.cost_management_unsupported_quota_ids, data.azurerm_subscription.current.quota_id)
  budgets_on        = var.budgets_enabled == null ? local.budgets_supported : var.budgets_enabled

  budget_scopes = {
    build = {
      name   = "budget-platform-build"
      groups = [local.rg_global, local.rg_build, local.clusters.build.node_resource_group]
    }
    nonprod = {
      name   = "budget-platform-nonprod"
      groups = concat(values(local.rg_tier["nonprod"]), [local.clusters.nonprod.node_resource_group])
    }
    prod = {
      name   = "budget-platform-prod"
      groups = concat(values(local.rg_tier["prod"]), [local.clusters.prod.node_resource_group])
    }
  }

  # Actual spend at 80 % and 100 %, forecast at 100 %.
  budget_notifications = [
    { threshold = 80, threshold_type = "Actual" },
    { threshold = 100, threshold_type = "Actual" },
    { threshold = 100, threshold_type = "Forecasted" },
  ]
}

resource "azurerm_consumption_budget_subscription" "platform" {
  for_each = { for k, v in local.budget_scopes : k => v if local.budgets_on }

  name            = each.value.name
  subscription_id = local.subscription_resource_id
  amount          = var.budget_amounts[each.key]
  time_grain      = "Monthly"

  time_period {
    # The first day of the month of the first apply. Azure keeps a start date once set, so the value
    # is not re-evaluated afterwards.
    start_date = formatdate("YYYY-MM-01'T'00:00:00'Z'", plantimestamp())
  }

  filter {
    dimension {
      name     = "ResourceGroupName"
      operator = "In"
      values   = sort(each.value.groups)
    }
  }

  dynamic "notification" {
    for_each = local.budget_notifications

    content {
      enabled        = true
      threshold      = notification.value.threshold
      threshold_type = notification.value.threshold_type
      operator       = "GreaterThanOrEqualTo"
      contact_roles  = ["Owner"]
      contact_emails = var.budget_contact_emails
    }
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}
