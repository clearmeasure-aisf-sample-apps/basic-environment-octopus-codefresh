# Lab 20: Promotion, Approvals, Freezes and the Hotfix Channel

**Curriculum Section:** Section 06 (Operate/Execute - Release Management)
**Estimated Time:** 40 minutes
**Type:** Process
**Builds on:** Lab 07 (pull requests), Lab 18 (follow a commit)

---

## Objective

Explain how a release of an app moves from `tdd` to `prod`: which transitions are automatic, which need a person, which person, when a deployment is blocked, and how an urgent fix reaches production without skipping the audit trail. Replace the legacy `force_skip_tdd` habit with the auditable `Hotfix` channel.

Lifecycles, channels, teams and the freeze are platform objects shared by every app (design §7.0); each app's process decides which of its steps run where. App #1, `workorders`, is the worked example.

## Background

All promotion decisions live in Octopus (ADR-D13): one audit trail, one calendar. Codefresh never deploys or approves; Argo CD never chooses a version.

![Dynamic: the deployment process of app #1 by environment](../../design/diagrams/dyn-deployment-process.png)

*Dynamic, the deployment process of app #1 in the order of `deployment_process.ocl` (steps 0 to 12), split by target environment. Every deployment first deploys `platform-wake`. In tdd it reads the acceptance secrets, pins, verifies, runs the acceptance tests and reports `platform/tdd`. In uat it pins, verifies and ends with the sign-off and its guard. In prod the go/no-go, the separation-of-duties guard and the pre-release backup come before the pin. The hotfix justification runs in uat and prod on channel `Hotfix` only, through a variable run condition.*

**Lifecycles and channels** (every app project; the starter OCL scaffolds both channels)

| Channel | Lifecycle | Phases | Who creates releases | Release number |
|---|---|---|---|---|
| `Default` | `platform-standard` | `tdd` (automatic) → `uat` (manual) → `prod` (manual) | Only the app's Codefresh release pipeline, with the `AISF-Service-Account` key (ADR-IR32) | The build's `<VERSION>`, equal to the package versions |
| `Hotfix` | `platform-hotfix` | `uat` → `prod` | Team `Release Managers` | `<package-version>-hotfix.<n>`, over packages that already exist |

`Hotfix` accepts only releases from Git reference `refs/heads/main` of the environment repo. No channel carries package-version rules: they would name the app's own packages, which the app owns. A branch build (`-ci.<sha7>`) still never reaches Octopus, because only the release pipeline, triggered by the default branch, creates releases. The conformance fixture `sandbox` adds a third channel, `Strict`, which sets `Platform.SoDMode` to `enforce`.

**Human and policy gates**

| Step | Where | Who | What it proves |
|---|---|---|---|
| `hotfix-justification` | `Hotfix` channel, right after `wake-environment` | `Release Managers` | Why the normal path is skipped |
| `prod-go-no-go` | `prod`, first gate | `Prod Approvers` | Someone accountable accepted the release for prod |
| `sod-guard` (step template `platform-sod-guard`) | `prod`, before any change | Script | Separation of duties per `Platform.SoDMode`, and the automation user's reason |
| `uat-signoff`, then `uat-signoff-guard` | `uat`, after verification | `UAT Approvers` | UAT was tested; the lifecycle then allows `prod` |
| `prod-weekend-freeze-<project>` | `prod`, Saturday 00:00 to Monday 00:00 UTC; one project freeze per app project that deploys to prod | Override: `Release Managers`, with a reason | Weekday releases |

**Separation of duties with one operator** (ADR-IR34 decision 23). `Platform.SoDMode` in library set `Platform Environment`:
- `single-operator` (default): the deployment creator may approve, with a reason recorded in the intervention's notes;
- `enforce`: `sod-guard` fails when the go/no-go approver created the deployment.

`Platform.InterventionTestMode` (default `true`) lets the automation user answer interventions, but only with a `conformance:<run-id>` or `e2e:<run-id>` reason; the guards fail any other answer by it (CAP-OCT-004, CAP-OCT-005).

**Steps per environment** (project `workorders`; `wake-environment` runs first everywhere and wakes the environment's cluster through `platform-wake`, [06-sleep-and-wake.md](06-sleep-and-wake.md)):

| Environment | Default channel | Hotfix channel adds |
|---|---|---|
| `tdd` | `wake-environment` → `update-argo-cd-image-tags` and `read-deployment-secrets` (in parallel) → `verify-version` and `smoke-test` (in parallel) → `acceptance-tests` → `report-commit-status` | (not in the lifecycle) |
| `uat` | `wake-environment` → `update-argo-cd-image-tags` → `verify-version` and `smoke-test` (in parallel) → `uat-signoff` → `uat-signoff-guard` | `hotfix-justification` right after `wake-environment` |
| `prod` | `wake-environment` → `prod-go-no-go` → `sod-guard` → `pre-release-backup` → `update-argo-cd-image-tags` → `verify-version` and `smoke-test` (in parallel) | `hotfix-justification` right after `wake-environment` |

The migration is not an Octopus step: the PreSync Job `db-migrate` runs inside the sync that the pin commit starts (Lab 19).

## Steps (online)

Students observe with the `Developers` role; a release manager drives any action.

### Step 1: Read the lifecycle

Project `workorders` → Channels. Find the lifecycle of each channel, the three phases of `platform-standard` and the two of `platform-hotfix`. Note which phase deploys automatically.

### Step 2: Follow a release from tdd to uat

Open a release that reached `uat`. In its uat deployment, find `uat-signoff`: who signed off, when, and with which note. Confirm that no prod deployment of that release started before the sign-off.

### Step 3: Read a prod deployment

Open a prod deployment. Find the `prod-go-no-go` responsible user, the deployment creator, the value of `Platform.SoDMode` and the `sod-guard` output. Open the `pre-release-backup` log and note the backup's blob name in `<backup-storage-account-prod>`, container `workorders-prod`.

### Step 4: Read the freeze

Deployment freezes → `prod-weekend-freeze-workorders` (every app project that deploys to prod has its own `prod-weekend-freeze-<project>`). Note its schedule, its scope (`prod`, one project) and the audit entries of any override.

### Step 5: Walk a hotfix (release manager demonstrates)

1. The fix merges to the default branch through the normal pull request; Codefresh runs every gate and creates release `2.5.<n>` on `Default`, which deploys to `tdd` as usual.
2. The release manager creates a `Hotfix` release `2.5.<n>-hotfix.1` that selects packages `2.5.<n>`.
3. The hotfix deploys to `uat`: `hotfix-justification` asks why; then the uat steps and `uat-signoff`.
4. The hotfix deploys to `prod`, overriding the weekend freeze with a reason if needed.
5. The pin commit writes the **package** version `2.5.<n>`, not the release number; `verify-version` checks that the app reports the package version.

## Offline variant: predict every handoff

Use only the files of the environment repo: `octopus/terraform/{lifecycles,channels,freezes,teams}.tf`, `.octopus/apps/workorders/workorders/deployment_process.ocl`, `octopus/step-templates/sod-guard.ps1`, `contracts/platform-contracts.yaml`. Predict the outcome of each scenario, then check.

| # | Scenario | Prediction: what happens, which steps run, who acts | Check in |
|---|---|---|---|
| S1 | Tuesday 10:00, Codefresh creates release `2.5.740`. | | `lifecycles.tf` |
| S2 | Wednesday, a release manager deploys `2.5.740` to `uat`. | | `deployment_process.ocl` scoping |
| S3 | Friday 16:00, release manager A starts the `prod` deployment; prod approver B approves go/no-go. | | Prod steps |
| S4 | Same as S3, but A approves the go/no-go. Once with `Platform.SoDMode` = `single-operator`, once with `enforce`. | | `sod-guard.ps1` |
| S5 | Saturday 09:00, a regular `prod` deployment of `2.5.740`. | | `freezes.tf` |
| S6 | Sunday, a production bug. The fix merges; Codefresh creates `2.5.741`. How does it reach `prod`, with which release number, and what does the pin file say afterwards? | | `channels.tf`; §7.6 |
| S7 | A developer tries to create a release on `Default` by hand. | | `channels.tf`; team `CI Release Publishers` |
| S8 | A `Hotfix` release built from package `2.5.735` while `prod` already ran the contract migration from `2.5.738`. | | Lab 19 Part A |
| S9 | The nightly conformance run answers the sandbox's go/no-go as `AISF-Service-Account` with the note `conformance:<run-id>`. The next night someone answers as that user with the note `ok`. | | `sod-guard.ps1`; `Platform.InterventionTestMode` |
| S10 | A second app onboards. Which team, lifecycle or freeze needs a change? | | `teams.tf`; `freezes.tf`; [../onboarding.md](../onboarding.md) |

<details>
<summary>Answer key</summary>

- **S1.** The release deploys to `tdd` automatically: wake, secrets, pin commit, the PreSync migration and rollout, Argo CD healthy verification, version, smoke, acceptance tests, status. `uat` and `prod` wait for a person.
- **S2.** `wake-environment` → `update-argo-cd-image-tags` → `verify-version` → `smoke-test` → `uat-signoff` (team `UAT Approvers`) → `uat-signoff-guard`. Acceptance tests never run in `uat` (they wipe their database, ADR-C11). The release cannot enter `prod` until the uat phase succeeds, which includes the sign-off.
- **S3.** `wake-environment` (wakes `aks-platform-prod` if it sleeps) → `prod-go-no-go` (B approves) → `sod-guard` passes → `pre-release-backup` → pin commit to `envs/prod` → PreSync migration and rollout → healthy verification → version → smoke.
- **S4.** `single-operator`: the guard passes when A recorded a reason in the notes, and the audit shows that the creator approved. `enforce`: the guard fails because the approver created the deployment. It runs before the backup and the pin, so nothing has changed; another prod approver must approve a new deployment.
- **S5.** `prod-weekend-freeze` blocks it. A release manager may override with a recorded reason; routine releases wait until Monday.
- **S6.** `2.5.741` goes through every gate and deploys to `tdd` on `Default`. A release manager creates `2.5.741-hotfix.1` on `Hotfix` over packages `2.5.741`: `uat` (with `hotfix-justification` and `uat-signoff`), then `prod` (justification, go/no-go, guard, backup, pin), overriding the freeze with a reason. The pin file says `newTag: "2.5.741"` (the package version).
- **S7.** Refused by procedure, not by permission: `Default` releases are created only by Codefresh with the `AISF-Service-Account` key (ADR-IR32); the developer has no Release Creator role, and the audit log shows any other creator. A second release creator is how the EP20 and EP21 release-number collisions happened.
- **S8.** Dangerous: the older code may read a column the contract migration dropped. Hotfix packages must be at or above every contract migration already applied in the target environment.
- **S9.** The first passes: test mode is on and the reason names a run. The second fails at the guard, before anything changes: the automation user may answer only with a `conformance:` or `e2e:` reason (CAP-OCT-005).
- **S10.** None. Teams scope by environment only, both lifecycles are shared, and `octopus/terraform` adds every descriptor's projects to the freeze scope when it applies the onboarding.

</details>

## Expected Outcome

- A one-page promotion map: environment, trigger, gates, approving team.
- The hotfix path written as steps, with the release number and the pinned version distinguished.
- An explanation of what `Platform.SoDMode` trades away under `single-operator`, and when `enforce` is right.

## Discussion

1. What does the weekend freeze protect against that approvals alone do not?
2. Why is the hotfix built by the normal default-branch pipeline instead of a special branch build?
3. When Octopus Approvals reaches GA (R20), which step disappears, and what stays?
