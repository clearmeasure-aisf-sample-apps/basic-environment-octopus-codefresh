# terraform/build: the platform build cluster aks-platform-build (ADR-IR34 decision 16, "Build runner";
# design §7.0 "Terraform layers").
#
# A build cluster only: the Codefresh Runner (runtime <cf-runtime> = aks-platform-build/codefresh), no
# app workload and no data. Applied by the provisioner from an operator session (P1-04), after
# terraform/foundation created rg-platform-build and the global state account.
#
#   - AKS Free tier; Entra ID with Azure RBAC; local accounts disabled; automatic upgrades off
#     (upgrades are manual, while the builds pool is at zero).
#   - AKS-managed VNet and system-assigned identities. This layer creates no role assignment, and the
#     cluster's identities hold none outside rg-platform-build-aks-nodes (TB2, CAP-CF-012). Pipelines get
#     no cloud identity; the engine pulls step images such as platform/ci-dotnet with the cf-platform-pull
#     token of registry integration acr-platform-pull, so the kubelet identity needs no registry grant.
#   - Pool system: 1 x Standard_B2pls_v2, always on, untainted; it runs the runner agent.
#     Pool builds: Standard_D4as_v6, autoscaled 0 to 2, taint codefresh.io/builds=true:NoSchedule, for the
#     engine and dind pods (codefresh/runner/values.yaml selects kubernetes.azure.com/agentpool: builds).
#     The autoscaler removes a builds node after 10 idle minutes.
#   - rg-platform-build carries a CanNotDelete lock once the Owner runs -ApplyLocks. It refuses deletes
#     in the group, including a node-pool rotation (a vm_size change of builds, Q40): the Owner lifts
#     the lock for that apply and locks again.
#   - Cluster access: terraform/foundation grants Azure Kubernetes Service RBAC Cluster Admin on
#     rg-platform-build to the provisioner (Helm install of cf-runtime) and to platform-operators; the
#     cluster inherits both. sp-platform-conformance gets the interim admin there, later the
#     least-privilege roles on the cluster itself.
#
# State: build.tfstate in container tfstate of <tfstate-storage-account-global> (rg-platform-global).
# Run from terraform/build as the provisioner, with ARM_CLIENT_ID, ARM_CLIENT_SECRET, ARM_TENANT_ID and
# ARM_SUBSCRIPTION_ID exported:
#   cp build.tfvars.example build.auto.tfvars    # replace the <placeholders>; never commit
#   terraform init -input=false \
#     -backend-config="storage_account_name=sttfglobal<name_suffix>" \
#     -backend-config="container_name=tfstate" \
#     -backend-config="key=build.tfstate" \
#     -backend-config="use_azuread_auth=true"
#   terraform apply -input=false
# The account name is output tfstate.global.storage_account_name of terraform/foundation.
# Then the runner (docs/preview-codefresh.md): az aks get-credentials --resource-group rg-platform-build
# --name aks-platform-build, kubelogin convert-kubeconfig -l spn (or azurecli), and helm install
# cf-runtime 10.5.6 with codefresh/runner/values.yaml. Sessions behind a TLS-re-terminating proxy use
# az aks command invoke instead (run command stays enabled).

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    # Current major, checked on the registry 2026-09-24 (§7.10 provider pins).
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.6"
    }
  }

  # Partial configuration; the values are passed with -backend-config (see above).
  backend "azurerm" {}
}
