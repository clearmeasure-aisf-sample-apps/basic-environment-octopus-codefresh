# Worker pools (ADR-IR34 decision 18, §7.0). The static pools k8s-tdd, k8s-uat and k8s-prod are shared by every app
# for steps that must run inside a cluster (app #1's acceptance tests, pre-release backups, restores). terraform/tier
# installs one Kubernetes worker per environment (helm_release octopus-worker-<env>, namespace octopus-worker-<env>)
# and registers it into its pool at env-apply (P1-07, P1-08). A Kubernetes worker "is limited to modifying its local
# namespace" (E27); the platform grants its script service account what the backup and restore Jobs need.
#
# The built-in dynamic pool Hosted Ubuntu (slug hosted-ubuntu) runs every other step: Terraform, env-wake, env-sleep,
# platform-wake, the Argo CD image-tag step (it runs on a worker, and the default pool is Hosted Windows) and the
# checks. It is only looked up.

resource "octopusdeploy_static_worker_pool" "k8s" {
  for_each = toset(local.app_environments)

  name        = "k8s-${each.key}"
  description = "Kubernetes worker in namespace octopus-worker-${each.key} on aks-platform-${local.environment_tiers[each.key]}, shared by every app for in-cluster steps in ${each.key}."
  is_default  = false
}

data "octopusdeploy_worker_pools" "hosted_ubuntu" {
  partial_name = "Hosted Ubuntu"
  take         = 10

  lifecycle {
    postcondition {
      condition     = length([for p in self.worker_pools : p if p.name == "Hosted Ubuntu"]) == 1
      error_message = "The built-in dynamic worker pool 'Hosted Ubuntu' (slug hosted-ubuntu) is missing from the platform space."
    }
  }
}

# Machine policy for the Kubernetes workers (ADR-IR33; the task cap of ADR-IR34). A sleeping cluster takes its workers
# offline for hours, and the instance runs at most 5 tasks at once, so:
# - health checks: never scheduled (health_check_interval 0, "no automatic health checks" in provider 1.20.0). A
#   worker keeps the status of its last check through a sleep, and no health check task holds a slot every hour
#   against a stopped cluster. env-wake requests one check, without waiting, only for a worker that reports anything
#   but healthy; Octopus checks a newly registered worker by itself [VERIFY that interval 0 means never].
# - connectivity: unavailable workers do not fail health checks;
# - cleanup: unavailable workers are never deleted;
# - updates: Octopus keeps upgrading the Kubernetes agent (E30) after a health check; Calamari on the next deployment.
# terraform/tier registers each worker with this policy (chart value agent.machinePolicyName).
resource "octopusdeploy_machine_policy" "kubernetes_workers" {
  name        = "Sleep-tolerant Kubernetes workers"
  description = "Kubernetes workers k8s-tdd, k8s-uat and k8s-prod sleep with their cluster (ADR-IR33): no scheduled health checks, offline workers never fail a check and are never deleted."

  machine_health_check_policy {
    health_check_interval = 0
    health_check_type     = "RunScript"

    bash_health_check_policy {
      run_type = "InheritFromDefault"
    }

    powershell_health_check_policy {
      run_type = "InheritFromDefault"
    }
  }

  machine_connectivity_policy {
    machine_connectivity_behavior = "MayBeOfflineAndCanBeSkipped"
  }

  machine_cleanup_policy {
    delete_machines_behavior = "DoNotDelete"
  }

  machine_update_policy {
    calamari_update_behavior         = "UpdateOnDeployment"
    kubernetes_agent_update_behavior = "Update"
    tentacle_update_behavior         = "NeverUpdate"
  }
}
