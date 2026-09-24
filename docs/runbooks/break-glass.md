# Break-glass

Emergency access that bypasses a normal control of the platform, and how to close it again. Every path here is
time-bound, recorded in an incident, and leaves evidence in `log-platform-<tier>`, Entra and Octopus.

Contracts: ADR-D11 and ADR-IR2 (Kyverno exceptions), ADR-D1 (local accounts off), ADR-IR34 (decision 25: no PIM,
group `platform-operators`, locks by the Owner script; §7.0 identities), ADR-IR33 (sleep and wake).

## Roles

| Role | Who | Rights used here |
|---|---|---|
| Responder | Group `platform-operators` (the user) | Standing AKS RBAC Cluster Admin on `rg-platform-nonprod-aks`, `rg-platform-prod-aks` and `rg-platform-build`; Key Vault Secrets Officer on the tier `-aks` and `-apps` groups (foundation). No PIM (decision 25) |
| Approver | A second person when one exists | Agrees in the incident record before any write |
| Azure Owner | The subscription Owner (the user) | Only one who can lift or restore a `CanNotDelete` lock (Owner script `docs/owner/Grant-ProvisionerRights.ps1 -ApplyLocks`) |
| Security owner | CODEOWNERS of `policies/**` | Reviews every exception within one business day |

Under the single-operator model (§13) the responder is also the approver: the incident record states the reason
before the action, and the review happens afterwards.

## Preconditions

- An incident record exists (`<incident-id>`) with the impact, the reason the normal path fails and the planned end.
- The normal path was tried first: a release through Octopus, a pull request to the environment repository, or a
  rollback (`rollback-and-forward-fix.md`).
- Break-glass never uses the provisioner's secret, the org-wide GitHub PAT or any shared password in a cluster.
- The tier is awake and stays awake. Paths A to C need the API server, which a sleeping cluster does not have: run
  `env-wake` for the tier, then pause sleeping for the incident, because work in `kubectl` or Argo CD is not an
  Octopus task and the hourly `env-sleep` would stop the cluster under the responder. Without Octopus, start it in
  Azure (`sleep-and-wake.md`, force-wake and emergency pause).

## Paths

| Situation | Path |
|---|---|
| Kyverno denies an image or workload that must run now | [A. Time-bound PolicyException](#a-time-bound-policyexception) |
| Kyverno is down and its fail-closed webhook blocks prod syncs | [B. Kyverno outage](#b-kyverno-outage) |
| Argo CD SSO or Octopus is unavailable and the cluster needs a human | [C. Direct cluster access](#c-direct-cluster-access) |
| An approved change is blocked by a `CanNotDelete` lock | [D. Lift a lock](#d-lift-a-lock) |

### A. Time-bound PolicyException

Kyverno honours exceptions only with `features.policyExceptions.enabled: true` and
`features.policyExceptions.namespace: kyverno` in the `kyverno` add-on (ADR-IR2); whether they govern the CEL
`PolicyException` kind is [VERIFY] (Q18).

1. Sign in to the cluster (Entra ID only; local accounts are disabled). Behind a TLS-re-terminating proxy, use
   `az aks command invoke` instead (the run command is enabled for this fallback):

   ```bash
   az login
   az aks get-credentials --resource-group rg-platform-<tier>-aks --name aks-platform-<tier> --format azure
   kubelogin convert-kubeconfig --login azurecli
   ```

2. Create the exception for exactly one workload and one policy, expiring within 4 hours. `expiresAt` makes Kyverno
   ignore it afterwards; step 6 deletes it.

   ```yaml
   apiVersion: policies.kyverno.io/v1
   kind: PolicyException
   metadata:
     name: breakglass-<incident-id>
     namespace: kyverno
     labels:
       policies.platform/break-glass: "true"
   spec:
     policyRefs:
       - kind: ImageValidatingPolicy
         name: verify-app-release-signatures
     matchConditions:
       - name: one-workload
         expression: >-
           object.metadata.namespace == '<app>-prod' &&
           object.kind == 'Deployment' &&
           object.metadata.name == '<deployment>'
     expiresAt: "<RFC 3339 UTC time, at most 4 hours ahead>"
     properties:
       reason: "<one line from the incident record>"
       ticket: "<incident-id>"
   ```

   Policy names of `policies/kyverno`: `verify-app-release-signatures` (kind `ImageValidatingPolicy`),
   `restrict-app-image-paths`, `require-mssql-express`, `disallow-latest-tag`, `require-resources`, `require-probes`
   and `disallow-privileged` (kind `ValidatingPolicy`). The app's own signer policy comes from the tenant chart; find
   its name with `kubectl get imagevalidatingpolicies`. An exception exempts the whole object from the named policy.

   ```bash
   kubectl apply -f breakglass-<incident-id>.yaml
   ```

3. Let Argo CD retry the sync of the app's Application, or sync it from the Argo CD UI.
4. Record in the incident: the exception name, the image digest that ran and why it could not come from the app's
   release pipeline.
5. Replace the workload with a signed release as soon as possible: a normal or Hotfix-channel release through Octopus.
6. Delete the exception, even if it has expired:

   ```bash
   kubectl delete policyexception -n kyverno breakglass-<incident-id>
   ```

### B. Kyverno outage

In prod the policies fail closed, but only for namespaces labelled `tier=app`. Platform namespaces keep working, so
Argo CD can repair Kyverno itself.

Within minutes of a wake, denials are usually the admission warm-up, not an outage: the webhook configurations
survive the stop and the admission controller is not Ready yet (`sleep-and-wake.md`, "After a wake"). Wait for it
before step 3.

1. Check the engine:

   ```bash
   kubectl -n kyverno get pods
   kubectl get validatingwebhookconfigurations | grep kyverno
   ```

2. Restore it through Git first: sync Application `kyverno` in Argo CD, or roll back the last change to
   `argocd/clusters/<tier>/addons/kyverno.yaml` by pull request.
3. Only if Kyverno cannot start and prod must change before it does: delete the Kyverno validating webhook
   configuration that serves the policies (names start with `kyverno-` [VERIFY the exact names for CEL policies in
   Kyverno 1.19]). Admission is then unenforced for every app namespace until Kyverno recreates the configuration on
   start. Record the time.
4. Verify recovery: pods ready, the webhook configurations exist again, and
   `kubectl get imagevalidatingpolicies,validatingpolicies -L policies.platform/mode` shows the policies ready.

### C. Direct cluster access

Use when Argo CD SSO or Octopus is down and a read or a targeted fix is needed. Desired state still comes from Git:
any change made here is reverted by self-heal unless it is also merged.

1. Sign in with `az aks get-credentials ... --format azure` and `kubelogin` (path A, step 1), or use
   `az aks command invoke --resource-group rg-platform-<tier>-aks --name aks-platform-<tier> --command "<kubectl …>"`.
   There is no admin kubeconfig: local accounts are disabled on all three clusters.
2. Read first (`kubectl get`, `kubectl logs`, `kubectl describe`). Record every write in the incident.
3. For Argo CD itself: local `admin` stays disabled. If SSO is broken for long, re-enable admin by a reviewed pull
   request to `argocd/bootstrap/values-<tier>.yaml` (`admin.enabled`) and disable it again the same way afterwards.

### D. Lift a lock

`CanNotDelete` locks sit on `rg-platform-global` (state), `rg-platform-build` (registry and build cluster) and
`rg-platform-prod-data` (prod database disks), applied by the Owner script with `-ApplyLocks` (ADR-IR34, P1-03). They
also block deletes of child resources, for example a registry repository purge by hand or a prod disk replacement.

1. The Azure Owner records the change or incident reference.
2. Delete only the lock the change needs:

   ```bash
   az lock list --resource-group rg-platform-prod-data --output table
   az lock delete --name <lock-name> --resource-group rg-platform-prod-data
   ```

3. Run the approved change.
4. Restore the lock with the Owner script, never by hand, so the lock matches the script:

   ```powershell
   ./docs/owner/Grant-ProvisionerRights.ps1 -SkipEntra -ApplyLocks
   ```

## Verification

- `kubectl get policyexceptions -A -l policies.platform/break-glass=true` returns nothing.
- `az lock list --resource-group rg-platform-prod-data` shows the lock again (and likewise for `rg-platform-global`
  and `rg-platform-build`).
- `kubectl get imagevalidatingpolicies,validatingpolicies -L policies.platform/mode` shows the prod policies with
  mode `Enforce`.
- Sleeping is resumed: the pause is reverted or the trigger `env-sleep-hourly-<tier>` is enabled again.

## Audit evidence

Attach these to the incident record:

- Kubernetes audit events of the responder, from `log-platform-<tier>` (table `AKSAuditAdmin`; column names [VERIFY]
  against the workspace):

  ```kusto
  AKSAuditAdmin
  | where TimeGenerated between (datetime(<start UTC>) .. datetime(<end UTC>))
  | extend who = tostring(User.username)
  | where who has "<responder UPN>" or tostring(ObjectRef.resource) == "policyexceptions"
  | project TimeGenerated, who, Verb, RequestUri, ResponseStatus
  | order by TimeGenerated asc
  ```

- Run-command invocations and lock deletions from the Azure Activity Log of the affected groups.
- Kyverno policy reports for the affected namespace (`kubectl get policyreports -n <app>-<env> -o yaml`).
- The Octopus task log of any deployment made during the window, and of the `env-wake` run and the sleep pause that
  opened it.
- The security owner's review note, within one business day.
