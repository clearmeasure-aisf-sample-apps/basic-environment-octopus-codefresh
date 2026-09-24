# Feeds (§7.2, §7.5). The built-in feed (slug octopus-server-built-in) holds ChurchBulletin.Database and
# ChurchBulletin.AcceptanceTests; Codefresh pushes them with the automation user key (ADR-IR32).
#
# acr-workorders: Azure Container Registry feed over OIDC (E30, E38); no stored credential. Octopus reads
# workorders/ui-server and workorders/worker versions for release creation and the Argo CD step, and the Kubernetes
# workers pull platform/ci-dotnet step images (the node's kubelet identity also holds AcrPull, §5.2).
# Federated subject for id-octopus-acr-pull, created by terraform/foundation: space:<space-slug>:feed:acr-workorders
# [VERIFY format]; outputs.tf renders it for cross-checking.
#
# docker-hub: anonymous Docker Hub feed for the execution container octopusdeploy/worker-tools:<worker-tools-version>
# of the Terraform and provisioner-check steps on Hosted Ubuntu (§7.2, ADR-IR6). Name and slug are both
# docker-hub, which the runbooks reference. The image is pinned by version; anonymous pulls count against the
# Docker Hub rate limit of the dynamic worker's egress address [VERIFY].

resource "octopusdeploy_docker_container_registry" "docker_hub" {
  name                           = "docker-hub"
  feed_uri                       = "https://index.docker.io"
  api_version                    = "v2"
  download_attempts              = 3
  download_retry_backoff_seconds = 10
}

resource "octopusdeploy_azure_container_registry" "acr_workorders" {
  name                           = "acr-workorders"
  feed_uri                       = "https://${var.acr_login_server}"
  download_attempts              = 3
  download_retry_backoff_seconds = 10

  oidc_authentication = {
    client_id    = var.acr_pull_identity_client_id
    tenant_id    = var.azure_tenant_id
    audience     = "api://AzureADTokenExchange"
    subject_keys = ["space", "feed"]
  }
}
