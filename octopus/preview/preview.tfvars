# Phase 0 preview inputs for octopus/terraform (used only by apply-preview.sh; no secrets).
# octopus_url, octopus_space_id and the API key come from the environment (TF_VAR_*), never from this file.
#
# Every value below is a stand-in for an input that the targeted preview resources do NOT read. The resources that
# read them (OIDC accounts, the ACR feed, library-variable-set variables, the Codefresh OIDC identity) are not
# targeted, so no stand-in reaches Octopus. Phase 1 replaces this file with the real, untracked terraform.tfvars.

octopus_space_slug = "preview-not-used"

azure_tenant_id       = "preview-not-used"
azure_subscription_id = "preview-not-used"

deploy_identity_client_ids = {
  tdd  = "preview-not-used"
  uat  = "preview-not-used"
  prod = "preview-not-used"
}

lifecycle_identity_client_ids = {
  nonprod = "preview-not-used"
  prod    = "preview-not-used"
}

acr_login_server            = "preview-not-used.invalid"
acr_pull_identity_client_id = "preview-not-used"

workorders_environment = {
  tdd = {
    app_base_url    = "https://preview-not-used.invalid"
    key_vault_name  = "preview-not-used"
    sql_server_name = "preview-not-used"
    sql_database    = "preview-not-used"
    ai_openai_url   = "https://preview-not-used.invalid"
    ai_openai_model = "preview-not-used"
  }
  uat = {
    app_base_url    = "https://preview-not-used.invalid"
    key_vault_name  = "preview-not-used"
    sql_server_name = "preview-not-used"
    sql_database    = "preview-not-used"
    ai_openai_url   = "https://preview-not-used.invalid"
    ai_openai_model = "preview-not-used"
  }
  prod = {
    app_base_url    = "https://preview-not-used.invalid"
    key_vault_name  = "preview-not-used"
    sql_server_name = "preview-not-used"
    sql_database    = "preview-not-used"
    ai_openai_url   = "https://preview-not-used.invalid"
    ai_openai_model = "preview-not-used"
  }
}

workorders_infrastructure = {
  state_storage_account = "preview-not-used"
}


# platform_octopus_api_key is not set here: apply-preview.sh passes the Space Manager key as
# TF_VAR_platform_octopus_api_key only to satisfy the declaration. The preview creates the library variable set
# WorkOrders Platform Automation empty; Platform.OctopusApiKey itself is phase 1 (ADR-IR33). Project platform-wake
# exists without a process until phase 1 loads .octopus/platform-wake.

# Used by targeted resources, set to their real phase 1 values:
tdd_auto_deploy          = false # phase 1 value (§9); TDD stays manual
runbook_triggers_enabled = false # no triggers in the preview (the projects stay database-backed)
team_external_groups     = {}    # teams are created without members; members are added in phase 1
team_member_user_ids     = {}
