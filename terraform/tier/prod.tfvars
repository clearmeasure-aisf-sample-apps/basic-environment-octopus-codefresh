# Committed inputs of tier prod (ADR-IR14), passed by env-plan, env-apply as -var-file=prod.tfvars.
# Identifiers and sizing only, never a secret. prod.tfvars.example explains every value. The main loop replaces
# the <placeholders> with the provisioned values at P1-08.

tier            = "prod"
tenant_id       = "<AZURE_TENANT_ID>"
subscription_id = "<AZURE_SUBSCRIPTION_ID>"
location        = "<azure-region>"

platform_key_vault_name = "<kv-platform-prod>"

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
  vm_size    = "Standard_D4ds_v5"
  node_count = 1
}
apps_node_pool = {
  vm_size   = "Standard_D4ds_v5"
  min_count = 1
  max_count = 4
}
os_disk_size_gb = 128

# --- Platform vault and monitoring ------------------------------------------------------------------------------------
key_vault_allowed_ip_ranges = []
log_retention_days          = 30
# Add receivers by pull request: name = "address". Empty sends notifications to nobody.
oncall_email_receivers = {}

# --- Octopus worker (octopus-worker-prod; pool k8s-prod) ---------------------------------------------------------------
octopus_url                   = "<OCTOPUS_URL>"
octopus_space                 = "<octopus-space>"
octopus_worker_chart_version  = "<kubernetes-agent-chart-version>"
octopus_worker_machine_policy = "Sleep-tolerant Kubernetes workers"

# --- Argo CD bootstrap (argocd-prod) -----------------------------------------------------------------------------------
argocd_chart_version      = "<argo-cd-chart-version>"
argocd_apps_chart_version = "<argocd-apps-chart-version>"
env_repo_url              = "<ENV_REPO_URL>"
argocd_repo_private       = true

kubelogin_login_mode = "octopus-oidc"

tags = {}
