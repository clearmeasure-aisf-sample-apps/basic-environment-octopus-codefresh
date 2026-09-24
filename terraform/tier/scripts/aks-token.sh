#!/bin/sh
# terraform/tier/scripts/aks-token.sh: Kubernetes exec credential plugin of the kubernetes and helm providers when
# kubelogin_login_mode = "octopus-oidc" (providers.tf).
#
# The Octopus Terraform steps (env-plan, env-apply, env-destroy) authenticate azurerm with the OIDC account
# azure-platform-lifecycle-<tier>: Calamari sets ARM_CLIENT_ID, ARM_TENANT_ID and ARM_OIDC_TOKEN and deliberately
# leaves the Azure CLI logged out (Calamari.Terraform TerraformDeployBehaviour, checked 2026-09-24), so kubelogin's
# azurecli mode has nothing to use. This script exchanges the same Octopus token for an Entra access token of the AKS
# server application (client-credentials grant with a federated client assertion) and prints an ExecCredential.
# Needs curl and python3 (both in octopusdeploy/worker-tools). The token never reaches a command line: curl reads
# the assertion from standard input.
set -eu

: "${ARM_TENANT_ID:?ARM_TENANT_ID is not set: run Terraform with the Octopus Azure OIDC account}"
: "${ARM_CLIENT_ID:?ARM_CLIENT_ID is not set: run Terraform with the Octopus Azure OIDC account}"
: "${ARM_OIDC_TOKEN:?ARM_OIDC_TOKEN is not set: the account is not an OIDC account; use another kubelogin_login_mode}"

# Azure Kubernetes Service AAD Server: the audience of every AKS cluster with Entra ID.
server_app="6dae42f8-4368-4678-94ff-3960e28e3630"

response="$(printf '%s' "$ARM_OIDC_TOKEN" | curl --silent --show-error --max-time 60 \
  --request POST "https://login.microsoftonline.com/${ARM_TENANT_ID}/oauth2/v2.0/token" \
  --data-urlencode "client_id=${ARM_CLIENT_ID}" \
  --data-urlencode "scope=${server_app}/.default" \
  --data-urlencode "grant_type=client_credentials" \
  --data-urlencode "client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer" \
  --data-urlencode "client_assertion@-")"

printf '%s' "$response" | python3 -c '
import datetime, json, sys
body = json.load(sys.stdin)
if "access_token" not in body:
    sys.exit("Entra returned no access token: " + str(body.get("error_description", body.get("error", "unknown error"))))
expires = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(seconds=int(body.get("expires_in", 3600)) - 60)
json.dump({
    "apiVersion": "client.authentication.k8s.io/v1beta1",
    "kind": "ExecCredential",
    "status": {"token": body["access_token"], "expirationTimestamp": expires.strftime("%Y-%m-%dT%H:%M:%SZ")},
}, sys.stdout)
'
