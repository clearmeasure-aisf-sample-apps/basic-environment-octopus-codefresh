# Committed inputs of tier nonprod (ADR-IR14), passed by env-plan, env-apply and env-destroy as -var-file=nonprod.tfvars.
# Identifiers and sizing only, never a secret. nonprod.tfvars.example explains every value. The main loop replaces
# the <placeholders> with the provisioned values at P1-07.

tier            = "nonprod"
tenant_id       = "<AZURE_TENANT_ID>"
subscription_id = "<AZURE_SUBSCRIPTION_ID>"
location        = "<azure-region>"

# Globally unique; the same value as the foundation's platform-vault name for nonprod.
platform_key_vault_name = "<kv-platform-nonprod>"

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
# Add receivers by pull request: name = "address". Empty sends notifications to nobody.
oncall_email_receivers = {}

# --- Octopus workers (octopus-worker-tdd, octopus-worker-uat; pools k8s-tdd, k8s-uat) ------------------------------
octopus_url                   = "<OCTOPUS_URL>"
octopus_space                 = "<octopus-space>"
octopus_worker_chart_version  = "<kubernetes-agent-chart-version>"
octopus_worker_machine_policy = "Sleep-tolerant Kubernetes workers"

# --- Argo CD bootstrap (argocd-nonprod) -------------------------------------------------------------------------------
argocd_chart_version      = "<argo-cd-chart-version>"
argocd_apps_chart_version = "<argocd-apps-chart-version>"
env_repo_url              = "<ENV_REPO_URL>"
argocd_repo_private       = true

# The Octopus Terraform step authenticates with the OIDC account only (providers.tf, scripts/aks-token.sh). Operator
# sessions with a logged-in Azure CLI pass -var=kubelogin_login_mode=azurecli instead.
kubelogin_login_mode = "octopus-oidc"

tags = {}
