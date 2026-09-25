# Lab 19: Ship a Schema or Configuration Change Safely (Expand/Contract)

**Curriculum Section:** Section 05 (Team/Process Design - Database DevOps) and Section 06 (Operate/Execute)
**Estimated Time:** 50 minutes
**Type:** Build + Analyze
**Builds on:** Lab 04 (writing a migration), Lab 18 (follow a commit)

---

## Objective

Plan a database change and a configuration change so that every environment of an app keeps working at every moment: during the rollout, when the next environment still runs the old release, and after a rollback. Practise the expand/contract pattern for the schema and for configuration (Kustomize components, ADR-D6).

The rules hold for every app with a `database` in its descriptor. App #1, `workorders` (DbUp, SQL Server Express in a pod), is the worked example.

## Background

Four platform facts make expand/contract mandatory:

![Level 3 deployment: app #1 in its namespace](../../design/diagrams/c4-3-app-cluster-b.png)

*Level 3 deployment, app #1 in namespace `workorders-<env>`. The HTTPRoute `ui-server` attaches to the ListenerSet `workorders-<env>` in platform-ingress and forwards to the Deployment `ui-server`; the Deployment `worker` stays at zero replicas. On every sync of `workorders-app-<env>`, the PreSync Job `db-migrate` migrates the database before the rollout. The StatefulSet `db` runs SQL Server 2022 Express on the claim `data-db-0`, statically bound to `disk-workorders-<env>-db`, with the certificate `db-tls` from `platform-internal-ca` and a PostSync Job `db-init` for the logins. The Secrets `db-sa`, `db-migrator`, `db-app` and `workorders-app` come from the app vault through ExternalSecrets; the tenant quota and NetworkPolicies fence the namespace. The sandbox has the same shape with the Deployment `web` and no app secret.*

1. **Migrations run before rollout.** Octopus commits the new pins; Argo CD then runs the PreSync Job `db-migrate` in the app namespace with the pinned migrator image, and only after it succeeds rolls out the new pods (ADR-IR34 decision 1). During the rolling update the new schema serves the old pods: `maxUnavailable: 0` keeps an old pod until a new one is ready.
2. **Migrations are forward-only.** DbUp never runs down-scripts. Rollback is Octopus "redeploy previous release": the older migrator finds nothing new to run, and the older images start against the newer schema.
3. **Environments lag each other.** `tdd` gets every release automatically; `uat` and `prod` get it days later, after approval. A schema can be two releases ahead of the code in another environment's rollback target.
4. **A shared base changes every environment at once.** A merged change to `gitops/apps/<app>/<deployable>/base/` reaches `tdd`, `uat` and `prod` together, outside the Octopus lifecycle. Changes that must roll through the environments go in a component instead (ADR-D6).

The rule that follows: **every schema and every configuration must work with the release that is running and with the release a rollback would restore.**

## Part A: schema change (expand, migrate, contract)

Example: replace `WorkOrder.RoomNumber` with a more general `Location` column. A single-step rename would break the running release the moment `db-migrate` finishes. Split it across releases:

| Release | Migration | Code | Safe because |
|---|---|---|---|
| A (expand) | `032_AddLocationToWorkOrder.sql`: add `Location` as a nullable column with the same type as `RoomNumber` (see `029_ExtendWorkOrderRoomNumberLength.sql`), then copy `RoomNumber` into it | Writes both columns, reads `RoomNumber` | The old release ignores the new column |
| B (migrate) | None, or a re-copy of rows written by older releases | Reads `Location` (falls back to `RoomNumber`), writes both | Releases A and B both keep both columns filled |
| C (contract) | `03x_DropRoomNumberFromWorkOrder.sql` | Reads and writes `Location` only | Only after B is in `prod` and no rollback target below B remains |

### Step 1: Write the expand migration

```powershell
ls src/Database/scripts/Update/
```

The highest number is `031`, so the expand script is `032_AddLocationToWorkOrder.sql`. Use TABS for indentation. The migration only adds and copies; it never renames or drops.

### Step 2: Run it locally

```powershell
.\PrivateBuild.ps1
```

The private build runs DbUp against the local database and runs the tests, exactly as in Lab 04.

### Step 3: Predict the platform's behaviour

After the merge, release A (`2.5.<n>`) reaches `tdd` automatically. Answer before looking:
- Which object applies `032`, in which namespace, with which image, as which database login?
- If `032` fails in `tdd`, what does the environment repo contain afterwards, what does Argo CD show, and which version keeps serving?
- Release A is in `prod`. Release C, which drops `RoomNumber`, is in `tdd` only. A release manager redeploys a release older than A to `uat`. Does anything break? What if C had reached `uat` first?

### Step 4: Decide when the contract may ship

Release C may enter a lifecycle only when:
- release B (or later) is running in `prod`;
- no environment would roll back below B (Octopus shows the previous successful release per environment);
- no `Hotfix` release built from a package older than B is planned.

In `prod`, step `pre-release-backup` (step template `platform-db-backup`) backs the database up to Blob storage before the pin commit, and the nightly CronJob `db-backup-<app>-prod` covers the rest ([../runbooks/database-backup-and-restore.md](../runbooks/database-backup-and-restore.md)). Neither replaces expand/contract: a restore loses the writes made since, and there is no point-in-time restore.

## Part B: configuration change (components, ADR-D6)

Where an app's configuration lives:

| What | Where (app #1) | Changed by |
|---|---|---|
| Shared manifests (probes, resources, the migration Job) | `gitops/apps/workorders/app/base/` | Pull request; reaches all environments on merge |
| Per-environment values (config literals, the store name, Worker replicas) | `gitops/apps/workorders/envs/<env>/app/config/` | Pull request per environment |
| Roll-through changes to shared manifests | `gitops/apps/workorders/components/<change>/`, enabled per environment | Pull requests in `tdd` → `uat` → `prod` order, then folded into `base/` |
| Secrets | Vault `kv-<app>-<e>-<hash4>` → ClusterSecretStore `<app>-<env>` → ESO → Secret `<app>-app`; keys declared in `secrets[]` of the descriptor | Terraform generates them, or an operator replaces the stand-in; never Git |
| Namespace, quota, NetworkPolicies, stores | The tenant chart, from the descriptor | Platform owners; never the app's folders |

Configuration follows expand/contract too: **new configuration is added to every environment before the release that needs it reaches that environment**, and removed only after no running or rollback release reads it.

Example: switch the `ui-server` readiness probe from `/alive` to `/ready` once work item WI-01 adds the database-only `/ready` endpoint.

### Step 5: Expand the code first

The WI-01 release must be running in an environment before its probe points at `/ready`; otherwise new pods never become ready and the rollout stalls. Record which release introduced `/ready`.

### Step 6: Add the component

`gitops/apps/workorders/components/ready-probe/kustomization.yaml` (illustrative):

```yaml
apiVersion: kustomize.config.k8s.io/v1alpha1
kind: Component
patches:
  - target:
      kind: Deployment
      name: ui-server
    patch: |-
      - op: replace
        path: /spec/template/spec/containers/0/readinessProbe/httpGet/path
        value: /ready
```

A component is inert until an overlay references it.

### Step 7: Enable it one environment at a time

1. Pull request 1 adds `components: [../../../../components/ready-probe]` to `gitops/apps/workorders/envs/tdd/app/config/kustomization.yaml`. After the merge, Argo CD syncs `workorders-app-tdd` only; no Octopus release is involved. Watch the next tdd deployment's verification.
2. Pull request 2 does the same in `envs/uat/app/config`, after the WI-01 release is in `uat`.
3. Pull request 3 does the same in `envs/prod/app/config`, after the WI-01 release is in `prod`.

### Step 8: Fold into the base

One pull request changes the probe in `gitops/apps/workorders/app/base/ui-server.yaml` and removes the three component references. Render every environment before and after:

```bash
# on main, before the fold
for env in tdd uat prod; do kustomize build gitops/apps/workorders/envs/$env/app > /tmp/$env.before.yaml; done
# on the fold branch
for env in tdd uat prod; do kustomize build gitops/apps/workorders/envs/$env/app > /tmp/$env.after.yaml; done
for env in tdd uat prod; do diff -u /tmp/$env.before.yaml /tmp/$env.after.yaml && echo "$env: no change"; done
```

The rendered output must be identical to the output before the fold, so the fold changes nothing at runtime. It still needs platform-owner review (CODEOWNERS on `gitops/apps/`). A base change that *does* alter runtime behaviour needs the `all-environments` label as well.

### Step 9: Know the contract hazard

Once `prod` probes `/ready`, redeploying a release older than WI-01 to `prod` would leave new pods unready. The rollback target is now WI-01 or later, unless the configuration is reverted first. Write this into the release notes of the pull request that enables the component.

## Offline variant: predict every handoff

Use only the files of the environment repo (and the app repository for source paths). Fill in the predictions, then check.

| # | Question | Prediction | Check in |
|---|---|---|---|
| P1 | Name of the expand script and the directory that holds it | | App repository: `src/Database/scripts/Update/` |
| P2 | The object, namespace, image and database login that apply it in `uat` | | `gitops/apps/workorders/app/base/migrate.yaml`; `render workorders` |
| P3 | The vault key the migrator password comes from, and how it reaches the Job | | `contracts/platform-contracts.yaml` (`descriptors.appVault`); the tenant chart's stores |
| P4 | What `gitops/apps/workorders/envs/tdd/app/kustomization.yaml` contains after a failed `032` in `tdd`, and what serves traffic | | ADR-IR34 decision 1; CAP-GIT-010 |
| P5 | What happens in a rollback ("redeploy previous release"), and what the migration does in it | | `migrate.yaml`; DbUp journal |
| P6 | Which Argo CD Applications sync after pull request 1 of Step 7, and which after the fold | | `render workorders` (`applications`) |
| P7 | Which CODEOWNERS entry and which label apply to a `base/` pull request that changes behaviour | | `CODEOWNERS`; ADR-D6 |
| P8 | Which checks reject a component that points the app at another app's namespace or registry path | | the consistency checks (`Kit.Consistency`) C09; `Platform.Onboarding check` |
| P9 | The earliest release a `prod` rollback may target after Step 7.3 | | Step 9 |

<details>
<summary>Answer key</summary>

- **P1.** `032_AddLocationToWorkOrder.sql` in `src/Database/scripts/Update/` (the highest existing number is `031`).
- **P2.** The PreSync Job `db-migrate` of Application `workorders-app-uat`, in namespace `workorders-uat`, running `<acr-name>.azurecr.io/apps/workorders/db-migrator:<VERSION>` (pinned with the app images), as login `workorders_migrator`.
- **P3.** Vault key `db-migrator-password` in `kv-workorders-u-<hash4>`, generated by `terraform/apps/tier`. ESO syncs it through ClusterSecretStore `workorders-uat` into Secret `db-migrator`, which the Job reads. No database password reaches Octopus.
- **P4.** The new tags: the pin commit happened. The sync fails at the PreSync Job, Argo CD retries it, the healthy verification of `update-argo-cd-image-tags` fails the Octopus deployment, and the old pods keep serving. The fix is a forward fix or "redeploy previous release", which pins the old tags again.
- **P5.** The older release's process runs again and its pin commit writes the older tags. The PreSync Job runs the older migrator: every one of its scripts is already in the journal, so it changes nothing, and the older code runs on the newer schema. That is why migrations follow expand/contract.
- **P6.** After pull request 1: only `workorders-app-tdd`. After the fold: `workorders-app-tdd`, `-uat` and `-prod` reconcile, but nothing changes because the rendered output is identical.
- **P7.** `/gitops/apps/ @<org>/platform-owners`, plus the `all-environments` label when runtime behaviour changes.
- **P8.** consistency check C09 renders every app overlay and fails on a namespace outside `<app>-*`, a cluster-scoped kind, a foreign store or an image outside `apps/<app>/`. `Platform.Onboarding check` fails any app file that names another app's registry path, namespaces, stores, vaults or resource groups. The AppProject `app-<app>` refuses the same at sync time.
- **P9.** The WI-01 release, or a later one, unless the probe change is reverted first.

</details>

## Expected Outcome

- A three-release plan for a rename, with the gate for the contract release written down.
- A component-based rollout plan for a shared manifest change, and the zero-diff fold.
- The habit of asking "what runs against this schema or configuration if a rollback happens now?"

## Discussion

1. The platform moved migrations from Octopus to an Argo CD PreSync Job (ADR-IR34 decision 1, superseding ADR-C2). What did that remove (database passwords in Octopus, cross-namespace traffic), and what did it cost (Git names a version the cluster does not run while a migration fails)?
2. What would break if the readiness switch were merged straight into `base/`?
3. Which environments would a hotfix release built from an older package reach, and what does that mean for contract migrations (Lab 20)?
