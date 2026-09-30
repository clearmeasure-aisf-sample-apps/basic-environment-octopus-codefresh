# Committed inputs of tier prod (ADR-IR14), passed by env-plan, env-apply as -var-file=prod.tfvars.
# Identifiers and sizing only, never a secret. prod.tfvars.example explains every value. The main loop replaces
# the <placeholders> with the provisioned values at P1-08.

tier            = "prod"
tenant_id       = "40645332-2e20-4bca-882c-b2706f93ce44"
subscription_id = "4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69"
location        = "southcentralus"

platform_key_vault_name = "kv-platform-pr-i3aldz"

# --- Network: never peered; must not overlap nonprod ------------------------------------------------------------------
network = {
  address_space     = ["10.20.0.0/16"]
  aks_subnet_prefix = "10.20.0.0/22"
}
cluster_network = {
  pod_cidr       = "192.168.0.0/16"
  service_cidr   = "172.16.0.0/16"
  dns_service_ip = "172.16.0.10"
}
api_server_authorized_ip_ranges = []

# --- Cluster (system 1 x 4 vCPU, apps 1-4 x 4 vCPU) -------------------------------------------------------------------
kubernetes_version = null
system_node_pool = {
  vm_size    = "Standard_D4as_v6"
  node_count = 1
}
apps_node_pool = {
  vm_size   = "Standard_D4as_v6"
  min_count = 1
  max_count = 4
}
os_disk_size_gb = 64

# --- Platform vault and monitoring ------------------------------------------------------------------------------------
key_vault_allowed_ip_ranges = []
log_retention_days          = 30
# Add receivers by pull request: name = "address". Empty sends notifications to nobody.
oncall_email_receivers = {}

# --- Octopus worker (octopus-worker-prod; pool k8s-prod) ---------------------------------------------------------------
octopus_url                   = "https://clearmeasure.octopus.app"
octopus_space                 = "AI Software Factory - Prototype"
octopus_worker_chart_version  = "3.15.1"
octopus_worker_machine_policy = "Sleep-tolerant Kubernetes workers"

# --- Argo CD bootstrap (argocd-prod) -----------------------------------------------------------------------------------
argocd_chart_version      = "10.9.2"
argocd_apps_chart_version = "2.0.5"
env_repo_url              = "https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git"

kubelogin_login_mode = "octopus-oidc"

tags = {}
