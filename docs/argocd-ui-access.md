# Argo CD UI access

Each tier's Argo CD UI is published through the platform ingress, restricted to allow-listed client IPs.

| Tier | URL | Instance | Platform vault |
|---|---|---|---|
| nonprod (tdd, uat, previews) | https://argocd.20-114-71-237.sslip.io | `argocd-nonprod` | `kv-platform-np-i3aldz` |
| prod | https://argocd.20-225-152-33.sslip.io | `argocd-prod` | `kv-platform-pr-i3aldz` |

## Account

- Local Argo CD account `jeffrey` (`accounts.jeffrey: login`, RBAC `g, jeffrey, role:admin`) in
  `argocd/bootstrap/values-<tier>.yaml`. Local `admin` stays disabled.
- Each tier has its own password. The owner reads it from the tier's platform vault:

  | Vault secret | Content |
  |---|---|
  | `argocd-user-jeffrey-password` | Plaintext password, for the owner only. Nothing in the cluster reads it |
  | `argocd-user-jeffrey-password-bcrypt` | bcrypt hash (cost 10), merged into `argocd-secret` key `accounts.jeffrey.password` |
  | `argocd-user-jeffrey-password-mtime` | RFC3339 time of the last change, merged into `accounts.jeffrey.passwordMtime` |

  ```bash
  az keyvault secret show --vault-name <vault> --name argocd-user-jeffrey-password --query value -o tsv
  ```

  ExternalSecret `argocd-secret-persisted` (`argocd/clusters/<tier>/platform-secrets.yaml`, creationPolicy Merge)
  writes the hash and mtime. Rotation: `docs/runbooks/credential-rotation.md`, section 10.

## Route and allow-list

`gitops/platform/ingress/base/argocd-ui.yaml`, synced by add-on Application `platform-ingress`:

- ListenerSet `argocd` (namespace `platform-ingress`): HTTPS listener for `argocd.<apps-domain-<tier>>`, certificate
  `argocd-tls` from ClusterIssuer `letsencrypt-http01` (ZeroSSL in nonprod, Let's Encrypt in prod). The overlays
  `gitops/platform/ingress/overlays/<tier>` set the host name.
- HTTPRoute `argocd-server` (namespace `argocd`): plain HTTP to Service `argocd-server:80`. TLS terminates at the
  gateway, so `argocd-server` runs with `configs.params.server.insecure: true` and the Octopus Argo CD gateway connects
  with `plaintext: true`.
- SecurityPolicy `argocd-ui-allowlist` (namespace `argocd`): Envoy Gateway authorization on that HTTPRoute only,
  default Deny, Allow for `principal.clientCIDRs`. Non-listed clients get `403`. The Envoy Service keeps
  `externalTrafficPolicy: Local`, so Envoy sees the real client address.

### The allow-list is cluster-only

This repository is public, so client IP addresses are not kept in Git. The real list lives in each tier's platform
vault and only in the live SecurityPolicy:

| Where | What it holds |
|---|---|
| Key Vault secret `argocd-ui-allowlist` in `kv-platform-np-i3aldz` / `kv-platform-pr-i3aldz` | The allow-list of that tier: comma-separated CIDRs, for example `198.51.100.7/32,203.0.113.0/28`. The source of truth |
| Live `spec.authorization` of SecurityPolicy `argocd/argocd-ui-allowlist` | The same CIDRs, written by `scripts/argocd/set-argocd-ui-allowlist.ps1` |
| `gitops/platform/ingress/base/argocd-ui.yaml` | A deny-all placeholder: default Deny and one Allow rule for `192.0.2.0/32` (TEST-NET-1, RFC 5737, never routed) |

Application `platform-ingress` (`argocd/clusters/<tier>/addons/ingress.yaml`) ignores `/spec/authorization` of that
one SecurityPolicy (`ignoreDifferences` with sync option `RespectIgnoreDifferences=true`). Argo CD therefore neither
reports the live CIDRs as drift nor reverts them on self-heal or sync, and the Application stays Synced. The design
fails closed: a new or rebuilt cluster, or a recreated SecurityPolicy, starts with the placeholder, so the UI answers
`403` to everyone until the script runs. Never put a real address in Git, in a commit message or in a pull request;
the tiers can hold different lists.

**Adding, changing or removing an IP** (platform owner, with Key Vault Secrets Officer on the vault and patch rights on
`securitypolicies` in namespace `argocd`; the tier must be awake, `env-wake` first):

1. Update the secret. Keep one entry per client, IPv4 `/24` or narrower, IPv6 `/48` or narrower (the script refuses
   anything wider); record who owns each entry in the secret's tags or the change ticket, not here.

   ```bash
   az keyvault secret set --vault-name kv-platform-np-i3aldz --name argocd-ui-allowlist \
     --content-type 'text/plain; comma-separated CIDRs' --file ./allowlist.txt   # file holds e.g. 198.51.100.7/32
   ```

2. Write it into the cluster, first as a dry run (validated by the API server with `dryRun=All`):

   ```bash
   az aks get-credentials --resource-group rg-platform-nonprod-aks --name aks-platform-nonprod --file ./kc-nonprod
   pwsh -NoProfile -File scripts/argocd/set-argocd-ui-allowlist.ps1 -Tier nonprod -Kubeconfig ./kc-nonprod -WhatIf
   pwsh -NoProfile -File scripts/argocd/set-argocd-ui-allowlist.ps1 -Tier nonprod -Kubeconfig ./kc-nonprod
   ```

   The script reads the secret, validates every entry, replaces `spec.authorization` (default Deny, one Allow rule),
   and waits until Envoy Gateway reports the policy `Accepted`. It prints the number of entries, never the addresses.
   It signs in as the service principal in `ARM_CLIENT_ID`, `ARM_CLIENT_SECRET` and `ARM_TENANT_ID` when those are
   set, otherwise with the Azure CLI session. Use `-Tier prod` with a kubeconfig of `aks-platform-prod` for prod.

3. Check from the added client that the UI loads, and from any other address that it answers `403`.

The same two steps restore the list after a cluster rebuild or after the SecurityPolicy was deleted and recreated.
A candidate for later: an Octopus runbook per tier that runs the script, so the change leaves an audit entry there.

## Security note

This is an interim arrangement for a demo environment. It is a local password account with admin rights, reachable
only from allow-listed IPs (kept in Key Vault, not in this public repository). The intended end state is Entra ID SSO
(`oidc.config` in the values files; register `https://argocd.<apps-domain-<tier>>/auth/callback` as the redirect URI
on `<argocd-sso-app>`). Once SSO works, remove account `jeffrey`, its RBAC line, the two `argocd-secret-persisted` entries and the three vault secrets.
