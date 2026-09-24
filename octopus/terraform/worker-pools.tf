# Worker pools (§7.2, ADR-C2, ADR-D14).
#
# Static pools k8s-tdd, k8s-uat and k8s-prod hold one Kubernetes worker each. terraform/environment installs the
# workers as helm_release octopus-worker-<env> in namespace octopus-worker-<env> and registers them into these
# pools with the short-lived token Octopus.WorkerRegistrationToken; Octopus upgrades them afterwards. A Kubernetes
# worker "is limited to modifying its local namespace" (E27), so it reaches SQL, Key Vault and ui-server:8080
# without write access to Argo-managed namespaces.
#
# The built-in dynamic pool Hosted Ubuntu (slug hosted-ubuntu) runs the Terraform steps, env-wake, env-sleep, the
# platform-wake step and the wake steps of the runbooks (ADR-IR33); it is looked up only to prove that the slug used
# by the runbooks exists.

resource "octopusdeploy_static_worker_pool" "k8s" {
  for_each = toset(local.app_environments)

  name        = "k8s-${each.key}"
  description = "Kubernetes worker in namespace octopus-worker-${each.key} on aks-workorders-${each.key == "prod" ? "prod" : "nonprod"}. Runs ${each.key} steps and ${each.key == "prod" ? "infra-prod" : "infra-nonprod"} database-principal steps."
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

# Machine policy for the Kubernetes workers (ADR-IR33). A sleeping cluster takes its workers offline for hours, so:
# - connectivity: unavailable workers do not fail health checks ("Unavailable machines will not cause health checks
#   to fail", https://octopus.com/docs/infrastructure/deployment-targets/machine-policies);
# - cleanup: unavailable workers are never deleted;
# - updates: Octopus keeps upgrading the Kubernetes agent (E30); Calamari updates on the next deployment.
# Health checks keep the Octopus defaults; env-wake starts one after each wake.
# Enum values are [VERIFY] against provider 1.20.0 and the space: the phase 0 preview creates this policy.
# Assignment: terraform/environment registers each worker with this policy through the kubernetes-agent chart value
# agent.machinePolicyName (chart 3.15.1: "The machine policy to register the agent with"); workers registered
# earlier are moved to it in the Octopus UI (Infrastructure, Workers, Policy).
resource "octopusdeploy_machine_policy" "kubernetes_workers" {
  name        = "Sleep-tolerant Kubernetes workers"
  description = "Kubernetes workers k8s-tdd, k8s-uat and k8s-prod sleep with their AKS cluster (ADR-IR33): offline workers never fail health checks and are never deleted."

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
