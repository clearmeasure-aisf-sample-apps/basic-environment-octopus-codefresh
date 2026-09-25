# Database backup and restore

Back up an app's in-pod database to Blob storage, and restore it from a backup. Every app with a `database` in its
descriptor gets the same mechanism, whatever its engine component; the examples use `workorders` (SQL Server 2022
Express). There is no managed point-in-time restore: databases run in pods (ADR-IR34), so a restore returns to the
moment of a backup.

Contracts: ADR-IR34 (decisions 1, 8 and 11; "Databases in pods"), §7.0 ("Database", "Storage", tenant CronJobs),
directive §4, Q38 (static volumes), Q42 (`BACKUP … TO URL`), CAP-AZ-008 to CAP-AZ-010, CAP-OCT-015, ADR-IR33 (sleep and
wake).

![Level 3 deployment: add-ons and pools of an app cluster](../../design/diagrams/c4-3-app-cluster-a.png)

*Level 3 deployment, inside `aks-platform-<tier>`. The add-ons tolerate `CriticalAddonsOnly` and run on the one-node system pool: argocd, external-secrets, kyverno, cert-manager, octopus-argocd-gateway and platform-ingress (Envoy, Gateway `platform-gateway`). The apps pool holds the app namespaces, the Octopus workers `octopus-worker-<env>` and the platform-backup Jobs. Every connection starts inside the cluster: Argo CD polls main, the gateway dials Octopus over gRPC, the workers poll for work, ESO reads the vaults, and SQL Server writes backups to Blob storage with a SAS that the backup Job obtains.*

## How it works

| Piece | Where | What it does |
|---|---|---|
| Disk `disk-<app>-<env>-db` | `rg-platform-<tier>-data` (`terraform/apps/tier`) | Standard SSD, no zone; 8 GiB in `tdd` and `uat`, 32 GiB in `prod`. Bound by a static PersistentVolume of the tenant chart, so it survives sleep, namespace deletion and a cluster rebuild (CAP-AZ-008). `CanNotDelete` on `rg-platform-prod-data` (Owner script `-ApplyLocks`) |
| Logins | Vault `kv-<app>-<e>-<hash4>`: `db-sa-password`, `db-migrator-password`, `db-app-password` | Generated once by `terraform/apps/tier`, synced by ESO to Secrets `db-sa`, `db-migrator`, `db-app`; rotated by `rotate-db-passwords` (`credential-rotation.md` §4) |
| Scheduled backup | CronJob `db-backup-<app>-<env>` in `platform-backup` (tenant chart), `uat` and `prod` only | Nightly engine-native backup as service account `platform-backup/db-backup`, federated to `id-db-backup-<tier>` (Storage Blob Data Contributor on the tier backup account). A missed run (the cluster slept) runs at the next wake |
| Pre-release backup | Step template `platform-db-backup`, before the prod pin (CAP-OCT-015) | Creates a Job from the same CronJob on the shared pool `k8s-<env>` and waits; the release stops if it fails |
| Backups | Container `<app>-<env>` of `<backup-storage-account-<tier>>` (`rg-platform-<tier>-shared`) | One container per app-environment with a database (`terraform/apps/tier`); `tdd` has one for ad-hoc backups. Shared keys off; soft delete and retention by the foundation |
| Restore | Suspended CronJob `db-restore-<app>-<env>` in `platform-backup`; app runbook `db-restore` | Restores a chosen backup (default: the latest) over the app database with the app at zero replicas |

- **RPO** is 24 hours in `uat` and `prod` (the nightly schedule), shorter right after a release. `tdd` databases are
  disposable: every environment rebuild or acceptance run may reset them.
- **Engine-native.** SQL Server 2022 on Linux writes `BACKUP DATABASE … TO URL` with a user-delegation SAS minted by
  `id-db-backup-<tier>` [VERIFY in P1-10, Q42]. Fallback, if the container refuses `TO URL`: the Job backs up to a
  file on a volume on the same node (ReadWriteOnce allows it) and copies it with `azcopy`.
- **Sleep.** A sleeping database cannot change, so nothing is lost while the cluster sleeps; the backup and restore
  Jobs need the cluster awake.

## Roles

| Role | Who | Does |
|---|---|---|
| Operator | `SRE On-call` (runbook `db-restore` in `uat` and `prod`) or a platform engineer | Runs the restore, validates the data |
| Data owner | The app's owner | Chooses the backup, accepts the data lost since it |
| Approver | The responsible team of the restore's manual intervention | Answers the intervention. With one operator (`Platform.SoDMode` = `single-operator`), the operator may answer with a recorded reason |
| `platform-operators` | The user (Entra group) | Manual fallback in the cluster; Blob reads |

## Check the backups

```bash
az storage blob list --auth-mode login --account-name <backup-storage-account-<tier>> \
  --container-name <app>-<env> --query "sort_by([], &properties.creationTime)[-5:].{name: name, created: properties.creationTime, size: properties.contentLength}" \
  --output table
```

The newest entry of `uat` or `prod` is less than 26 hours old while the tier has been awake that night; the
conformance test `BackupTests` (CAP-AZ-009) checks it for `sandbox-uat` every night. With the cluster awake:

```bash
kubectl -n platform-backup get cronjobs,jobs -l platform/app=<app>
```

The label is set by the tenant chart [VERIFY the label key against gitops/platform/tenant].

## Back up now

Before a risky change, or before a restore (so the restore can be undone):

1. Wake the tier if it sleeps (`sleep-and-wake.md`, force-wake).
2. `uat` and `prod`: deploy the release again, or run any app process that contains `platform-db-backup`; or, as
   `platform-operators`:

   ```bash
   kubectl -n platform-backup create job db-backup-<app>-<env>-manual-$(date -u +%y%m%d%H%M) --from=cronjob/db-backup-<app>-<env>
   kubectl -n platform-backup wait --for=condition=complete --timeout=30m job/db-backup-<app>-<env>-manual-<stamp>
   ```

3. `tdd` has no CronJob: treat its database as disposable. Its container `<app>-tdd` exists for a backup taken by
   hand when a lesson needs one.

## Restore

### Preconditions

- An incident record: what was damaged, when, and the data owner's choice of backup (the last one before the damage).
- A backup of the current state (Back up now), unless the database is unreadable.
- The app is stopped, so no session writes during the restore: a pull request sets the app's replicas to 0 in
  `gitops/apps/<app>/envs/<env>/<deployable>/`, or the descriptor sets `status: frozen` (the tenant then sets
  workloads and the database to zero; unfreeze it for the restore Job's database). Argo CD shows the app's
  Applications Synced with no app pods.
- Deployments are paused: an Octopus deployment freeze for the app's projects in `<env>`.
- The tier is awake and stays awake (`sleep-and-wake.md`: force-wake, then pause sleeping if the restore is long).

### Steps

1. In Octopus, the app's project, run runbook `db-restore` in `<env>` and give the backup name (empty for the latest)
   [VERIFY the prompt names in `.octopus/apps/<app>/<project>/runbooks/db-restore.ocl`]. It waits for the tier, starts
   a Job from `db-restore-<app>-<env>`, and waits for it.
2. Validate before the app starts again, with the data owner: row counts and the latest business records around the
   backup time; the migration journal of the app (for `workorders`:
   `SELECT TOP 5 ScriptName, Applied FROM dbo.SchemaVersions ORDER BY Applied DESC`).
3. Schema: if the restored journal is older than the running release, the next sync's PreSync Job `db-migrate`
   re-applies the newer scripts (forward-only migrations, ADR-IR34 decision 1). If that is wrong for the data, redeploy
   the release that matches the backup (`rollback-and-forward-fix.md`).
4. Passwords: the logins in the backup carry the passwords of the backup time. If `rotate-db-passwords` ran since, run
   it again for the app in `infra-<tier>`, so the vault and the database agree (`credential-rotation.md` §4).
5. Start the app: revert the replica pull request (or set `status: active`), then lift the freeze. A sleeping cluster
   applies the revert only at its next wake.

### Manual fallback (Octopus unavailable)

As `platform-operators`, with the tier awake: render a Job from the suspended CronJob with the backup name, apply it,
and follow its log. The Job is a one-off outside Git; delete it afterwards and record it in the incident.

```bash
kubectl -n platform-backup get cronjob db-restore-<app>-<env> -o json \
  | jq --arg n "db-restore-<app>-<env>-manual" --arg b "<backup-name>" \
      '{apiVersion: "batch/v1", kind: "Job", metadata: {name: $n, namespace: "platform-backup"},
        spec: (.spec.jobTemplate.spec | .template.spec.containers[0].env |= (map(select(.name != "RESTORE_BLOB")) + [{name: "RESTORE_BLOB", value: $b}]))}' \
  | kubectl create -f -
kubectl -n platform-backup logs -f job/db-restore-<app>-<env>-manual
```

`RESTORE_BLOB` is the variable `restore.sh` of gitops/platform/components/backup/mssql reads: a blob name under the
container, for example `<app>/20260924T233000Z.bak`; empty restores the newest.

## Data after a cluster rebuild

`env-destroy` and `env-apply` (nonprod only) delete and recreate the cluster, never the disks (CAP-AZ-008).
`env-destroy` targets the cluster (`-target=azurerm_kubernetes_cluster.this`, terraform/tier/versions.tf "Rebuild"):
the static IPs, and so the apps domain, the workspace, the platform vault and the network stay.

1. Run `env-apply` with a fresh `Octopus.WorkerRegistrationToken`, so the workers of `k8s-<env>` register again. After
   it, run `apps-apply` for every app with a workload identity (new OIDC issuer).
2. Argo CD recreates the tenant; the static PersistentVolume binds `disk-<app>-<env>-db` again [VERIFY, Q38].
3. Check the disk: `az disk show -g rg-platform-<tier>-data -n disk-<app>-<env>-db --query diskState` shows
   `Attached` once the database pod runs. A disk that stays `Unattached` with the pod Pending points at the volume's
   binding; see the tenant chart's volume and `kubectl describe pvc -n <app>-<env>`.

## Verification

- The Job completed, and the app's health endpoint reports healthy after the start (for `workorders`, `/_healthcheck`).
- The data owner confirms the business checks in the incident record.
- The newest blob of `<app>-<env>` is the pre-restore backup or later.
- The fast-burn alert `slo-fast-burn-<app>-<env>` stays quiet for 30 minutes after writes resume (`slo-fast-burn.md`).

## Audit evidence

- The Octopus runbook run: the prompted backup, the intervention answer and who gave it.
- The Job logs in `platform-backup`, and the Kubernetes audit events in `log-platform-<tier>` (table `AKSAuditAdmin`).
- The Blob listing of the container before and after, and the storage account's diagnostic logs if enabled.
- The incident record with the backup used, the data lost (RPO) and the time to writes re-enabled (RTO).
