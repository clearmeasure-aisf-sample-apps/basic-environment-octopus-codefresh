# Inputs. Values live in an untracked terraform.tfvars (see terraform.tfvars.example); credentials only in TF_VAR_*.
# The P1-06 values and where each comes from: docs/preview-octopus.md.

# --- Octopus connection -------------------------------------------------------------------------------------

variable "octopus_url" {
  type        = string
  description = "Octopus Cloud URL, <OCTOPUS_URL>, without a trailing slash."
}

variable "octopus_space_id" {
  type        = string
  description = "ID of the platform space <octopus-space> (<octopus-space-id>)."
}

variable "octopus_space_slug" {
  type        = string
  description = "Slug of the platform space (<octopus-space-slug>). Renders the OIDC subjects in outputs.tf, which terraform/foundation must match."
}

variable "octopus_access_token" {
  type        = string
  description = "Octopus OIDC access token, if no API key is used. Set TF_VAR_octopus_access_token; never write it to a file."
  default     = null
  sensitive   = true
}

variable "octopus_api_key" {
  type        = string
  description = "The Space Manager API key (ADR-IR32). Set TF_VAR_octopus_api_key, or leave it null and set OCTOPUS_APIKEY for the provider."
  default     = null
  sensitive   = true
}

variable "repo_root" {
  type        = string
  description = "Root of the environment repository: apps/*.yaml and octopus/step-templates/*.ps1 are read from it. Null means two levels above this directory; octopus/apply.ps1 passes it because it runs a copy."
  default     = null
}

# --- Azure (identity client IDs are looked up read-only in azure.tf) ---------------------------------------------

variable "azure_tenant_id" {
  type        = string
  description = "<AZURE_TENANT_ID>."
}

variable "azure_subscription_id" {
  type        = string
  description = "<AZURE_SUBSCRIPTION_ID>."
}

variable "acr_login_server" {
  type        = string
  description = "Login server of the shared registry, <acr-name>.azurecr.io (terraform/foundation output)."
}

variable "tier_state_storage_accounts" {
  type        = map(string)
  description = "Names of <tfstate-storage-account-<tier>> (terraform/foundation outputs), keyed nonprod and prod: the backends of terraform/tier and terraform/apps/tier."

  validation {
    condition     = length(setsubtract(toset(["nonprod", "prod"]), toset(keys(var.tier_state_storage_accounts)))) == 0 && length(var.tier_state_storage_accounts) == 2
    error_message = "Provide exactly the keys nonprod and prod."
  }
}

variable "apps_domains" {
  type        = map(string)
  description = "Overrides of <apps-domain-<tier>>, keyed nonprod and prod, for a custom domain (R35). A tier not named here uses <ingress-ip-dashed-<tier>>.sslip.io from its ingress IP once terraform/tier has created it (azure.tf; ADR-IR34 decision 21)."
  default     = {}

  validation {
    condition     = length(setsubtract(toset(keys(var.apps_domains)), toset(["nonprod", "prod"]))) == 0
    error_message = "Only the keys nonprod and prod are allowed."
  }
}

# --- Stored objects: looked up by name, never created or managed (§5.3) --------------------------------------

variable "stored_git_credential_name" {
  type        = string
  description = "Name of the stored Git credential used for config as code and the pin commits; restricted to the environment repository (check stored_git_credential_restricted)."
  default     = "GitHub clearmeasure-aisf-sample-apps"
}

variable "stored_azure_account_name" {
  type        = string
  description = "Name of the stored client-secret account. No project uses it from P1 on (ADR-IR34 decision 3); it is only looked up."
  default     = "Azure Runtime Provisioner"
}

variable "stored_library_variable_set_names" {
  type        = list(string)
  description = "Stored library variable sets that must stay included in no project."
  default     = ["Azure Runtime Provisioning", "GitHub AISF Sample Apps"]
}

variable "env_repo_url" {
  type        = string
  description = "<ENV_REPO_URL>: the environment repository that holds .octopus/ config as code and the GitOps pins."
  default     = "https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git"
}

# --- Lifecycles and platform values -------------------------------------------------------------------------------

variable "tdd_auto_deploy" {
  type        = bool
  description = "Phase TDD of platform-standard deploys automatically (CAP-OCT-001)."
  default     = true
}

variable "platform_automation_display_name" {
  type        = string
  description = "Value of Platform.AutomationUsername (library variable set Platform Environment): the automation user, compared case-insensitively with the creator and approver usernames by platform-sod-guard."
  default     = "AISF-Service-Account"
}

variable "platform_intervention_test_mode" {
  type        = bool
  description = "Platform.InterventionTestMode: true lets the automation user answer manual interventions, only with the reason conformance:<run-id> or e2e:<run-id> (ADR-IR34 decision 23)."
  default     = true
}

variable "platform_sod_mode" {
  type        = string
  description = "Platform.SoDMode: single-operator (the creator may approve with a reason) or enforce (the creator may not approve)."
  default     = "single-operator"

  validation {
    condition     = contains(["single-operator", "enforce"], var.platform_sod_mode)
    error_message = "platform_sod_mode must be single-operator or enforce."
  }
}

# --- Secrets (TF_VAR_* only) ------------------------------------------------------------------------------------

variable "platform_octopus_api_key" {
  type        = string
  description = "The Space Manager API key (ADR-IR32) for PlatformWake.OctopusApiKey (library variable set Platform Automation) and the step-scoped Platform.OctopusApiKey of platform-infrastructure. Set TF_VAR_platform_octopus_api_key; never write it to a file. A key rotation re-applies this configuration (R23)."
  sensitive   = true
  nullable    = false

  validation {
    condition     = startswith(var.platform_octopus_api_key, "API-")
    error_message = "platform_octopus_api_key must be an Octopus API key (it starts with API-)."
  }
}

variable "infrastructure_key_scope" {
  type        = string
  description = "Scope of Platform.OctopusApiKey in platform-infrastructure: steps (runbook and step slugs, S5) or unscoped (every step of the project; the fallback when Octopus rejects the slugs as scope values, Q26)."
  default     = "steps"

  validation {
    condition     = contains(["steps", "unscoped"], var.infrastructure_key_scope)
    error_message = "infrastructure_key_scope must be steps or unscoped."
  }
}

variable "octopus_worker_registration_token" {
  type        = string
  description = "Optional stored value of the prompted sensitive variable Octopus.WorkerRegistrationToken of platform-infrastructure (short-lived; env-apply passes it to terraform/tier). Null stores an empty value, and the token is given at the prompt of the run that installs workers. Set TF_VAR_octopus_worker_registration_token."
  default     = null
  sensitive   = true
}

variable "argocd_repo_read_credential" {
  type        = string
  description = "Optional value of the sensitive project variable ArgoCD.RepoReadCredential of platform-infrastructure: the JSON read credential of the environment repository (the stored PAT until R11). Null leaves it to a person. Set TF_VAR_argocd_repo_read_credential."
  default     = null
  sensitive   = true
}

variable "e2e_github_token" {
  type        = string
  description = "Optional value of the sensitive project variable E2E.GitHubToken of platform-infrastructure, scoped to runbook e2e-pass: the org PAT of Codefresh context platform-conformance (clone of the environment repository, pull request of the end-to-end pass). Null leaves it to a person. Set TF_VAR_e2e_github_token."
  default     = null
  sensitive   = true
}

# --- Triggers ---------------------------------------------------------------------------------------------------

variable "env_sleep_triggers_managed" {
  type        = bool
  description = "Create the hourly env-sleep triggers here. false when provider 1.20.0 cannot manage triggers for runbooks stored in Git (Q27): the main loop then creates them through the REST API (docs/preview-octopus.md)."
  default     = true
}

variable "env_sleep_trigger_timezone" {
  type        = string
  description = "Time zone of the env-sleep triggers. An hourly schedule does not depend on it; the working window is evaluated inside env-sleep in Sleep.TimeZone (ADR-IR33)."
  default     = "UTC"
}

# --- People -----------------------------------------------------------------------------------------------------

variable "team_external_groups" {
  type        = map(list(string))
  description = "External security group IDs (for example Entra group object IDs) per team name."
  default     = {}
}

variable "team_member_user_ids" {
  type        = map(list(string))
  description = "Octopus user IDs per team name, besides the automation user."
  default     = {}
}

variable "automation_username" {
  type        = string
  description = "Username of the existing automation user (display name AISF-Service-Account), read by name and never created (ADR-IR32)."
  default     = "aisf-service-account"
}

variable "automation_user_teams" {
  type        = list(string)
  description = "Teams that hold the automation user. CI Release Publishers creates releases; UAT Approvers and Prod Approvers let the conformance and end-to-end runs answer interventions with a run reason (ADR-IR34); Platform Engineers lets the main loop approve env-apply and apps-apply at provisioning time."
  default     = ["CI Release Publishers", "UAT Approvers", "Prod Approvers", "Platform Engineers"]
}

# --- Prod freeze ------------------------------------------------------------------------------------------------

variable "prod_freeze_first_window" {
  type = object({
    start = string
    end   = string
  })
  description = "First occurrence of prod-weekend-freeze (RFC 3339): a Saturday 00:00 to the following Monday 00:00. It repeats weekly for every app project that deploys to prod."
  default = {
    start = "2026-10-03T00:00:00Z"
    end   = "2026-10-05T00:00:00Z"
  }
}
