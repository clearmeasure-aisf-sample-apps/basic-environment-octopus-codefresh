# Feeds (ADR-IR34 §7.0). The built-in feed (slug octopus-server-built-in) holds ChurchBulletin.AcceptanceTests of app #1.
#
# acr-apps: Azure Container Registry feed over OIDC for the shared registry <acr-name> (renamed from the never-applied
# acr-workorders). Octopus reads apps/<app>/<image> versions for release creation and the image-tag step, and the
# Kubernetes workers pull platform/ci-dotnet step images (their nodes' kubelet identity also holds AcrPull). No stored
# credential. Federated subject of id-octopus-acr-pull, created by terraform/foundation: space:<octopus-space-slug>:feed:acr-apps
# [VERIFY format]; outputs.tf renders it for cross-checking.
#
# docker-hub (live, kept): anonymous Docker Hub feed for the execution container
# octopusdeploy/worker-tools:<worker-tools-version> on hosted-ubuntu. Name and slug are both docker-hub, which the OCL
# references. Anonymous pulls count against the Docker Hub rate limit of the dynamic worker's egress address [VERIFY].

resource "octopusdeploy_docker_container_registry" "docker_hub" {
  name                           = "docker-hub"
  feed_uri                       = "https://index.docker.io"
  api_version                    = "v2"
  download_attempts              = 3
  download_retry_backoff_seconds = 10
}

resource "octopusdeploy_azure_container_registry" "acr_apps" {
  name                           = "acr-apps"
  feed_uri                       = "https://${var.acr_login_server}"
  download_attempts              = 3
  download_retry_backoff_seconds = 10

  oidc_authentication = {
    client_id    = data.azurerm_user_assigned_identity.acr_pull.client_id
    tenant_id    = var.azure_tenant_id
    audience     = "api://AzureADTokenExchange"
    subject_keys = ["space", "feed"]
  }
}
