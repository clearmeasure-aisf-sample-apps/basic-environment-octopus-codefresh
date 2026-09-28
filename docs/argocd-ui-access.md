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

**Changing the allow-list:** edit the `clientCIDRs` list in `gitops/platform/ingress/base/argocd-ui.yaml`. That one
list covers both tiers. Use one commented `/32` entry per client, then commit to `main`. `platform-ingress` syncs it
within about a minute.

## Security note

This is an interim arrangement for a demo environment. It is a local password account with admin rights, reachable
only from allow-listed IPs. The intended end state is Entra ID SSO (`oidc.config` in the values files; register
`https://argocd.<apps-domain-<tier>>/auth/callback` as the redirect URI on `<argocd-sso-app>`). Once SSO works, remove
account `jeffrey`, its RBAC line, the two `argocd-secret-persisted` entries and the three vault secrets.
