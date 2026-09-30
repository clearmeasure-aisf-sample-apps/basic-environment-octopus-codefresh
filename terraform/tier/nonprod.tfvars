# Committed inputs of tier nonprod (ADR-IR14), passed by env-plan, env-apply and env-destroy as -var-file=nonprod.tfvars.
# Identifiers and sizing only, never a secret. nonprod.tfvars.example explains every value. The main loop replaces
# the <placeholders> with the provisioned values at P1-07.

tier            = "nonprod"
tenant_id       = "40645332-2e20-4bca-882c-b2706f93ce44"
subscription_id = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69"
location        = "southcentralus"

# Globally unique; the same value as the foundation's platform-vault name for nonprod.
platform_key_vault_name = "kv-platform-np-i3aldz"

# --- Network: never peered; must not overlap prod ---------------------------------------------------------------
network = {
  address_space     = ["10.10.0.0/16"]
  aks_subnet_prefix = "10.10.0.0/22"
}
cluster_network = {
  pod_cidr       = "192.168.0.0/16"
  service_cidr   = "172.16.0.0/16"
  dns_service_ip = "172.16.0.10"
}
# P1 starts without authorized ranges (Q7): Entra ID with Azure RBAC, local accounts off.
api_server_authorized_ip_ranges = []

# --- Cluster (ADR-IR34 "Clusters and capacity": system 1 x 4 vCPU, apps 1-7 x 4 vCPU) ------------------------------
kubernetes_version = null
system_node_pool = {
  vm_size    = "Standard_D4as_v6"
  node_count = 1
}
apps_node_pool = {
  vm_size   = "Standard_D4as_v6"
  min_count = 1
  max_count = 7
}
os_disk_size_gb = 64

# --- Platform vault and monitoring ------------------------------------------------------------------------------------
key_vault_allowed_ip_ranges = []
log_retention_days          = 30
# Never commit receivers (e-mail addresses) or IP ranges: the repository is public (ADR-IR14, guard CommittedTfvarsGuardTests). If ever used, pass them as Octopus variables TF_VAR_*. Empty sends notifications to nobody.
oncall_email_receivers = {}

# --- Octopus workers (octopus-worker-tdd, octopus-worker-uat; pools k8s-tdd, k8s-uat) ------------------------------
octopus_url                   = "https://clearmeasure.octopus.app"
octopus_space                 = "AI Software Factory - Prototype"
octopus_worker_chart_version  = "3.15.1"
octopus_worker_machine_policy = "Sleep-tolerant Kubernetes workers"
# tdd script pods run the acceptance suite (one Chromium per NUnit worker). Measured 2026-09-28 on the single apps node
# (Standard_D4as_v6, allocatable 3860m CPU, 14.5 GiB): 3004m CPU and 10.8 GiB requested, about 200m CPU in use, so
# 856m CPU is unrequested. A script pod cannot move to another node (ReadWriteOnce workspace), so its request has to
# fit beside a rollout that Read deployment secrets now overlaps (surge ui-server 250m + worker 100m): 400m. No CPU
# limit, so the pod bursts into the idle cores; the memory limit bounds the Chromium processes. uat keeps the default.
octopus_worker_script_pod_resources = {
  tdd = {
    requests = { cpu = "400m", memory = "2Gi" }
    limits   = { memory = "6Gi" }
  }
}

# --- Argo CD bootstrap (argocd-nonprod) -------------------------------------------------------------------------------
argocd_chart_version      = "10.9.2"
argocd_apps_chart_version = "2.0.5"
env_repo_url              = "https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git"
argocd_repo_private       = true

# The Octopus Terraform step authenticates with the OIDC account only (providers.tf, scripts/aks-token.sh). Operator
# sessions with a logged-in Azure CLI pass -var=kubelogin_login_mode=azurecli instead.
kubelogin_login_mode = "octopus-oidc"

tags = {}
