# Build duration of the build of record

P1 exit criterion 8 for app #1 ([cutover-and-decommission.md](../cutover-and-decommission.md), design §9): the build of
record, `workorders/release`, excluding the acceptance (Playwright) gate, takes at most 1.2 times the legacy
`build-linux` plus publish (GitHub Actions, `.github/workflows/build.yml` of the app repo). The acceptance gate must
pass but is timed separately: the legacy publish jobs never waited for acceptance tests (owner decision of
2026-09-25, like for like). This runbook gives the measurement method, the timings of 2026-09-25,
the changes made from them and the expected result.

Contracts: design §9 (P1 exit criteria), §7.7 (gate and release step names), Q40 and V03 (one `Standard_D4as_v6`
builds node), ADR-IR33 (sleep and wake).

## Method

Durations come from the Codefresh API with the account key (`CODEFRESH` in the operator's environment; never on a
command line, never printed). Nothing below starts a build.

1. `GET https://g.codefresh.io/api/builds/<build id>`: `created`, `started`, `finished`, `status` and `progress`
   (the progress id). `started - created` is queue time: plan BASIC_1 runs one build at a time, so a build waits for
   any other build of the account (`pendingLicense: true`), and a build on an empty `builds` pool also waits for a node.
2. `GET https://g.codefresh.io/api/progress/<progress id>`: `location.url`, a signed storage URL of the step log.
3. `GET <location.url>` (no header): JSON with `steps[]`, each with `name`, `status`, `creationTimeStamp`,
   `finishTimeStamp` (epoch seconds) and `logs`. `GET /api/progress/download/<progress id>` returns the same logs
   as one HTML page.
4. A step's real start is the timestamp of its log line `Continuing execution`, not `creationTimeStamp`: in parallel
   mode Codefresh creates every step when it first evaluates the step's `when`, so `creationTimeStamp` of `package` is
   the build start. A step without `when` starts at `creationTimeStamp`. Its end is `finishTimeStamp`.
5. The build duration is `finished - started` (queue time excluded, node start included, because "Initializing
   Process" runs after `started`).

```sh
B=<build id>
P=$(curl -s -H "Authorization: $CODEFRESH" "https://g.codefresh.io/api/builds/$B" | jq -r .progress)
U=$(curl -s -H "Authorization: $CODEFRESH" "https://g.codefresh.io/api/progress/$P" | jq -r .location.url)
curl -s "$U" -o "progress-$B.json"
python3 - "progress-$B.json" <<'PY'
import json, re, sys
from datetime import datetime, timezone
d = json.load(open(sys.argv[1])); s0 = d['data']['started']
steps = d['steps'].values() if isinstance(d['steps'], dict) else d['steps']
rows = []
for st in steps:
    if not st.get('finishTimeStamp'):
        continue
    logs = st.get('logs') or {}
    text = ''.join(logs.values() if isinstance(logs, dict) else logs)
    start = st.get('creationTimeStamp') or st['finishTimeStamp']
    m = re.search(r'\[(\S+)Z\] Continuing execution', text)
    if m:
        start = datetime.fromisoformat(m.group(1)).replace(tzinfo=timezone.utc).timestamp()
    rows.append((start - s0, st['finishTimeStamp'] - s0, st['name']))
for a, b, n in sorted(rows):
    print(f"{n:22s} {a/60:5.1f} -> {b/60:5.1f} min ({(b-a)/60:4.1f})")
print(f"total {(d['data']['finished'] - s0)/60:.1f} min")
PY
```

The legacy side: `GET https://api.github.com/repos/<owner>/<repo>/actions/runs/<run id>/jobs` gives `started_at` and
`completed_at` of `Integration Build (SQL container)` (`build-linux`) and of the publish jobs
(`docker-build-image-for-churchbulletin-ui`, `publish-github-packages`, `publish-octopus`, all `needs: build-linux`).
The legacy critical path of a master push is `changes`, then `build-linux`, then the slowest publish job.

## Before: timings of 2026-09-25

`workorders/release` build `6ab689102379153f0a5f78eb` (master `b15efaa`, VERSION 2.5.722, warm node, queue 7 s) and
`workorders/ci` build `6ab68283721535efb055b2b1` (branch `e2e/r20260925t1417-629a466c`, queued 6.4 min behind
`env-checks`, not a cold node). Minutes from the build start.

| Step | release: start → end (duration) | ci: start → end (duration) |
|---|---|---|
| Initializing Process | 0.0 → 0.7 (0.7) | 0.0 → 0.4 (0.3) |
| main_clone, platform_clone | 0.7 → 0.8 (0.1) | 0.4 → 0.7 (0.3) |
| prepare | 0.9 → 1.0 (0.2) | 0.7 → 2.1 (1.3) |
| wake_nonprod | 1.1 → 1.4 (0.4) | — |
| build_sql (chain A) | 1.1 → 5.8 (4.8) | 2.2 → 7.2 (4.9) |
| acceptance (chain A, after build_sql) | 5.8 → 19.8 (14.0) | 7.2 → 21.4 (14.2) |
| code_analysis (chain B) | 1.1 → 3.5 (2.5) | 2.2 → 4.9 (2.7) |
| build_sqlite (chain B) | 3.5 → 6.0 (2.5) | 4.9 → 7.4 (2.5) |
| qodana (chain B) | 6.0 → 11.0 (5.0) | 7.5 → 12.7 (5.2) |
| security_scan (chain B) | 11.1 → 12.3 (1.2) | 12.7 → 14.0 (1.3) |
| gate | 19.9 → 19.9 (0.1) | 21.4 → 21.5 (0.1) |
| package | 19.9 → 33.6 (13.7) | — |
| stage_images, image_reuse | 33.6 → 34.0 (0.4) | — |
| ui_image, worker_image, migrator_image | 34.0 → 35.0 (1.0) | — |
| supply_chain | 35.0 → 35.6 (0.7) | — |
| octopus_preflight … octopus_release | 35.6 → 36.4 (0.8) | — |
| **Total** | **36.5** | **21.5** |

The other master releases of 2026-09-24 and 2026-09-25 agree: `6ab5606db4147be81913cc3a` 34.4 min,
`6ab569db9cd4c6b62aa32422` 33.5, `6ab622ed332d50e0c6720158` 34.3, each with `package` at 13.6 to 13.7 min and
`acceptance` at 13.9 to 14.2.

Findings:

- **`package` spent most of its 13.7 minutes on a progress bar.** `PackageAcceptanceTests` copies `.playwright`
  (343 files, 536 MB) into the publish folder with `Copy-Item`. PowerShell draws the copy progress in the step's
  terminal and queries the cursor position (`ESC[6n`) twice per update; nothing answers, and each query waits about a
  second: 684 queries for 339 updates between 15:07:01 and 15:18:53 (11.9 min, which also holds the pack of the
  460 MB package, under a minute at the other packages' rate). GitHub Actions has no terminal, so the legacy job
  never paid this.
- **The critical path was chain A**: `build_sql` then `acceptance`, 18.8 min, while chain B finished 7.5 min earlier.
  `acceptance` builds its own worktree and needs nothing from `build_sql`; the chain existed only so that the two SQL
  Servers never share port 1433.
- **`package` waited for the gate** although it only writes files on the build volume.
- Smaller items, unchanged: image builds (about 1 min, in parallel; the layer cache lookup fails with "You dont have
  access for pull image" and the builds run without it, but every layer after `COPY built/` changes with each build,
  so a BuildKit registry cache would save seconds); NuGet restores are warm (1 to 2 s per project, cache on the
  pipeline volume); Chromium is baked into `platform/ci-dotnet`.

## Legacy baseline

No measured `build-linux` plus publish duration exists yet:

- The app repo `clearmeasure-aisf-sample-apps/20260923-001` has no workflow runs (GitHub Actions stays disabled there,
  ADR-IR26; workflow `Build`, id 366232502, `total_count: 0` on 2026-09-25).
- The upstream `ClearMeasureLabs/bootcamp-palermo-workorders` holds the history, but its Actions API was not reachable
  from the measuring session (proxy: repository not enabled). [VERIFY: read the jobs of the last 20 master pushes of
  workflow `build.yml` with the call in Method and record the p95 of `changes` + `build-linux` + slowest publish job
  here.]
- The only legacy durations on record are three whole-workflow runs of 2026-09-24 in the design session transcript
  (code-changing branch pushes, so no publish jobs): 35940562468 13.2 min, 35940596282 13.8 min, 35940644713
  13.4 min. They bound every job of those runs, including `build-linux` and `acceptance-tests`, from above.

Estimate until the [VERIFY] is done: `build-linux` (build, unit and integration tests, CRAP, `Package-Everything`,
artifact upload of the 460 MB AcceptanceTests package) 8 to 10 min, plus publish (artifact download, Docker build and
push, Octopus push) 3 to 4 min: **11 to 14 min**, a 1.2× budget of **13 to 17 min**.

## Changes

| Change | Where | Expected saving on the release |
|---|---|---|
| `$ProgressPreference = "SilentlyContinue"` for `Package-Everything` | `codefresh/apps/workorders/pipelines/release.yml` step `package`; the same line in `codefresh/templates/dotnet-buildps1/pipelines/release.yml` | 11.5 to 12 min |
| Chains rebalanced: chain A `acceptance` alone, its SQL Server on port 1434 (`MSSQL_TCP_PORT`, `SQL_SERVER_HOST=localhost,1434`); chain B `build_sql` → `code_analysis` → `build_sqlite` → `qodana`; `security_scan` beside them; both SQL Servers capped at 2 GiB (`MSSQL_MEMORY_LIMIT_MB`) | `pipelines/ci.yml`, `pipelines/release.yml` | 4 to 5 min (ci and release) |
| `package` and `stage_images` after `build_sql`, beside the other gates; `image_reuse` waits for `gate` and `stage_images`, so every push, signature and Octopus call still follows a passing gate | `pipelines/release.yml` | 2 min |

Unchanged: signing (`cosign.sign`), SBOM and provenance, the tag lock, the reuse path of a re-run (CAP-CF-014), the
handoff, `wake_nonprod` (release only; `workorders/ci` wakes nothing), the runner and the `builds` pool.

## After: expected

| Segment | Expected (min) |
|---|---|
| Initializing Process, clones, prepare | 1.0 |
| Chain A `acceptance` | 14.0 to 15.0 (more CPU contention than before) |
| Chain B `build_sql` 4.8 → `code_analysis` 2.5 → `build_sqlite` 2.5 → `qodana` 5.0 | 14.8 |
| `package` + `stage_images` (beside, after `build_sql`) | 2.0, off the critical path |
| `gate`, `image_reuse`, images, `supply_chain`, handoff | 3.0 |
| **Total, warm node** | **about 19 to 20** (was 33.5 to 36.5) |
| `workorders/ci`, warm node | about 17 (was 21.5) |

A cold `builds` pool (the first build after 10 idle minutes) adds the node start to Initializing Process [VERIFY
Q51: 3 to 5 min]. Keeping one node warm (`builds` minimum 1) would remove that at the cost of one
`Standard_D4as_v6` around the clock [VERIFY price; roughly USD 125 to 145 a month pay-as-you-go]; the pool minimum
is not changed.

Measured after: `workorders/release` build `6ab7195012c13d5efe845c13` of 2026-09-26, in
[After: measured](#after-measured-2026-09-26) below.

## Verdict on criterion 8

**Owner decision of 2026-09-25: like for like.** The timed build excludes the acceptance (Playwright) gate; the
acceptance gate must still pass and is timed separately, like the legacy `acceptance-tests` job, which the legacy
publish jobs never waited for (`needs: [changes, build-linux]`). This is option 2 below, with every other gate kept in the timed build.

- **Timed build.** `finished - started` of `workorders/release` minus the part of step `acceptance` that extends
  past everything else before `gate`. Equivalently: Initializing Process, clones and `prepare`, then the longest of
  chain B and `package` + `stage_images`, then `gate` to the handoff. Expected about 19 min (1.0 + 14.8 + 3.0 from
  the table above), because chain B (`build_sql`, `code_analysis`, `build_sqlite`, `qodana`) stays in the timed build;
  the 9 min of option 2 below counts `build_sql` and the tail only.
- **Acceptance.** Step `acceptance` passes; its duration is recorded next to the timed build, with no limit in
  criterion 8.
- **Budget.** 1.2 times the legacy `build-linux` plus publish: 13 to 17 min on the estimate above, which stays
  [VERIFY] until the legacy jobs are read.

Expected: about 19 min against a budget of 13 to 17 min, so criterion 8 still turns on the measured legacy baseline
and on chain B. Before the decision, with `acceptance` on the critical path, the expectation was about 19 to 20 min
and the verdict **not expected to pass**: the critical path was `acceptance` (160 Playwright tests, 11 m 44 s of test time with 4 NUnit workers,
set by the app's `src/AcceptanceTests/AcceptanceTests.runsettings`) followed by the release tail. The legacy
publish jobs do not wait for `acceptance-tests` (`needs: [changes, build-linux]`), so the legacy build of record is
shorter by construction. Options considered:

1. App work item: more NUnit workers or a split acceptance run on the build node, measured against the flakiness
   note in the runsettings (#9019). An app change, outside this repository.
2. A like-for-like comparison: Codefresh `build_sql` + release tail (about 9 min) against `build-linux` + publish,
   with acceptance compared to the legacy `acceptance-tests` job separately. **Taken by the owner on 2026-09-25**, with every gate
   except `acceptance` kept in the timed build.
3. `Standard_D8as_v6` builds nodes (the Q40 fallback) and three chains: about twice the build-node cost per hour.

## Fallback: qodana beside the chains (branch `p1/qodana-parallel`)

If the measured legacy baseline puts the budget under the timed build of about 19 min, the fallback moves `qodana`
out of chain B: it starts right after `prepare`, beside `acceptance`, `build_sql` and `security_scan`, in both
`workorders/ci` and `workorders/release`. Chain B becomes `build_sql` → `code_analysis` → `build_sqlite`. `gate`
waits for `build_sqlite`, `qodana_result`, `acceptance` and `security_scan` (all `finished`) and `gate.ps1` still
requires the qodana marker, so `image_reuse` (after `gate` success and `stage_images` success), and with it every push,
signature and Octopus call, still waits for every gate, qodana included.

Expected from the step table above (minutes from the build start, warm node):

| Segment | Now | Fallback |
|---|---|---|
| Initializing Process, clones, prepare | 1.0 | 1.0 |
| Chain B | 14.8 (`build_sql` 4.8, `code_analysis` 2.5, `build_sqlite` 2.5, `qodana` 5.0) | 9.8, plus 0.5 to 1.5 of contention while three heavy steps overlap |
| `qodana` | in chain B | 1.0 → about 6.5 to 7.5, off the critical path |
| `package` + `stage_images` (after `build_sql`) | 5.8 → 7.8 | the same, off the critical path |
| `gate`, `image_reuse`, images, `supply_chain`, handoff | 3.0 | 3.0 |
| **Timed build (acceptance excluded)** | **about 19** | **about 14 to 15.5**: a saving of 3.5 to 5 min |
| Whole release, acceptance included | about 19 to 20 | about the same: chain A (`acceptance`, 14 to 15) then becomes the longest branch |
| `workorders/ci`, warm node | about 17 | about the same, bounded by `acceptance` |

Cost: for its first five minutes the node carries `acceptance` (Chromium, UI.Server, SQL Server on 1434),
`build_sql` (SQL Server on 1433) and the Qodana linter at once, three heavy steps instead of two, still with two
SQL Servers. Watch `acceptance` for Playwright timeouts and the dind pod for OOM kills; the rollback is
`when.steps: build_sqlite finished` on `qodana` again and `build_sqlite` off the `gate` join.

## Risks

- Two SQL Servers, Chromium and a .NET build share one node for the first five minutes. Watch `acceptance` for
  Playwright navigation timeouts and the dind pod for OOM kills (V03); the rollback is to put `acceptance` back
  after `build_sql` (`when.steps: build_sql finished`) and the SQL Server back on 1433.
- Port 1434 relies on `MSSQL_TCP_PORT` of the SQL Server image and on the `host,port` form of `SQL_SERVER_HOST` in
  `build.ps1` (`Get-DefaultDatabaseServer`); the DbUp console trusts the certificate because the name contains
  `localhost` (`DatabaseConnectionStringBuilder.IsLocalServer`).
- `package` now runs when `build_sql` passes even if a later gate fails; it publishes nothing, but its log shows up in
  failed builds.

## 2026-09-26: acceptance back after build_sql

The first ci run with acceptance beside build_sql (`6ab7072c718b2dc51632cc3c`, master `b15efaa`) failed in acceptance before any test ran. Its SQL Server service on port 1434 never became ready: the step ended 2.5 minutes after start with no output, and every other gate passed (build_sql 915 unit and 337 integration tests, build_sqlite, code_analysis, qodana beside the chains, security_scan). Acceptance went back to running after build_sql, with its own SQL Server on 1433 (the rollback above). Qodana stays beside the chains. The timed build of criterion 8 excludes acceptance, so the rollback does not change it; the whole release keeps the acceptance chain of about 19 minutes. [VERIFY] the cause, for example the service's port mapping under `shared_host_network` or memory with three heavy steps, before trying 1434 again.

## After: measured 2026-09-26

`workorders/release` build `6ab7195012c13d5efe845c13`: master `e3db0f4` (the merge of `20260923-001` PR #5),
VERSION 2.5.723, created 01:01:17.8Z, started 01:01:52.3Z (queue 35 s, `pendingLicense: false`), finished
01:25:26.5Z, `success`. Pipeline layout at that commit of this repository: `qodana` beside the chains (`9531fa9`),
`acceptance` after `build_sql` with its SQL Server on 1433 (`a850886`), the `package` progress stall fixed
(`f30c632`). Log from `GET /api/builds/<id>` → `.progress` → `GET /api/progress/download/<progress>`
(`6ab7194f12c13d5efe845b74`) and the progress JSON of Method.

A step starts at its `Continuing execution` line and ends at its `Successfully ran` line. Exceptions:
Initializing Process and the clones have no `Continuing execution` line (start `creationTimeStamp`, end
`finishTimeStamp`); the image steps sign after their last `Successfully ran push step`, so they end at
`finishTimeStamp` (9 to 17 s later). Minutes from the build start (01:01:52.3Z), next to the "before" release
`6ab689102379153f0a5f78eb`:

| Step | after: start → end (duration) | before: start → end (duration) |
|---|---|---|
| Initializing Process | 0.0 → 0.4 (0.4) | 0.0 → 0.7 (0.7) |
| main_clone, platform_clone | 0.4 → 0.5 (0.1) | 0.7 → 0.8 (0.1) |
| prepare | 0.5 → 0.6 (0.1) | 0.9 → 1.0 (0.2) |
| wake_nonprod | 0.7 → 1.1 (0.5) | 1.1 → 1.4 (0.4) |
| qodana (beside the chains), qodana_result | 0.7 → 5.2 (4.4, then 0.1) | 6.0 → 11.0 (5.0, chain B) |
| security_scan | 0.7 → 2.3 (1.7) | 11.1 → 12.3 (1.2, chain B) |
| build_sql (chain A and chain B) | 0.7 → 6.0 (5.3) | 1.1 → 5.8 (4.8) |
| acceptance (chain A, after build_sql, SQL Server on 1433) | 6.2 → 20.3 (14.2) | 5.8 → 19.8 (14.0) |
| code_analysis (chain B, after build_sql) | 6.2 → 9.1 (2.9) | 1.1 → 3.5 (2.5) |
| build_sqlite (chain B) | 9.2 → 13.0 (3.8) | 3.5 → 6.0 (2.5) |
| package (after build_sql) | 6.2 → 9.8 (3.6) | 19.9 → 33.6 (13.7, after gate) |
| stage_images | 9.8 → 10.1 (0.3) | 33.6 → 34.0 (0.4, with image_reuse) |
| gate | 20.5 → 20.6 (0.1) | 19.9 → 19.9 (0.1) |
| image_reuse | 20.7 → 20.8 (0.2) | see stage_images |
| ui_image, worker_image, migrator_image (build, push, cosign sign) | 20.8 → 22.1 (1.2) | 34.0 → 35.0 (1.0) |
| supply_chain | 22.1 → 22.8 (0.7) | 35.0 → 35.6 (0.7) |
| octopus_preflight … octopus_release | 22.8 → 23.5 (0.6) | 35.6 → 36.4 (0.8) |
| **Total (`finished - started`)** | **23.6** | **36.5** |
| **Timed build (acceptance excluded)** | **16.0** | **28.9** |

Timed build, per the like-for-like rule (Verdict on criterion 8 above; criterion 8 of
[cutover-and-decommission.md](../cutover-and-decommission.md)): `finished - started` (1414.2 s) minus the time from
the end of the last gate other than `acceptance` (`build_sqlite`, `Successfully ran` 01:14:51.8Z) to the start of
`gate` (`Continuing execution` 01:22:24.7Z), 452.9 s: **961.3 s, 16.0 min**. Equivalently: 0.7 min of Initializing Process,
clones and `prepare` to the start of the chains, chain B 12.3 (to 13.0), then `gate` to `finished` 3.0. Through the handoff
(`octopus_release` `Successfully ran` 01:25:19.7Z) it is 15.9 min. Every gate except `acceptance` is inside it,
`qodana` and `security_scan` included, and `package` + `stage_images` end at 10.1, off its critical path. The
"before" timed build by the same rule: 36.5 − (19.9 − 12.3) = 28.9 min.

Acceptance, timed separately: 14.2 min (6.2 → 20.3), `success`; `ACCEPTANCE BUILD SUCCEEDED - Build time:
00:13:28`. It stays the longest branch, so the whole release is 23.6 min.

Against the expectations:

- `package` 3.6 min (was 13.7): the `$ProgressPreference` fix removed the progress-bar stall (no `ESC[6n` query in
  the log).
- Chain B took 12.3 min (0.7 → 13.0) against 9.8 plus 0.5 to 1.5 of contention expected: `build_sql` 5.3 (4.8
  before) beside `qodana` and `security_scan`, then `code_analysis` 2.9 (2.5) and `build_sqlite` 3.8 (2.5) beside
  `acceptance` and `package`, plus about 0.2 min of scheduling between steps. Timed build 16.0 against the
  expected 14 to 15.5.
- The tail from `gate` to `finished` took 3.0 min, as expected.

### Verdict against the budget

**Within the estimated budget of 13 to 17 min, in its upper part: 16.0 min.** The budget is 1.2 times an estimated
legacy `build-linux` plus publish of 11 to 14 min, which stays [VERIFY] (Legacy baseline above). A timed build of
16.0 min passes criterion 8 if the measured legacy baseline (p95 of `changes` + `build-linux` + slowest publish job)
is at least 13.4 min (16.0 / 1.2), and fails if it is lower. Criterion 8 stays open on that reading. The next lever,
if needed, is chain B's contention with `acceptance` (about 2.5 min above the uncontended step times).

### The rest of criterion 8 for this build

- **Release created exactly once.** Octopus holds `Releases-49489`, 2.5.723, assembled 01:25:19.2Z, release notes
  line 1 `app-commit: e3db0f4b7046564b072bdcd8522aa711deb2a1c5`. The project's events for it: one `Created`
  (`Events-1569244`, 01:25:19.4Z, from 20.118.80.199, the build node's egress, user agent `octopus/2.26.0
  (release;create)`, the `octopus release create` of step `octopus_release`, which logged
  `Successfully created release version 2.5.723` at 01:25:19.6Z), then the tdd deployment (`DeploymentQueued`,
  `DeploymentStarted`); no `Deleted`. Pass.
- **Images signed.** `image_reuse` found none of the six tags (`2.5.723`, `sha-e3db0f4` of `ui-server`, `worker`,
  `db-migrator`), so the build built them; each image step pushed both tags and then signed keylessly with the
  Codefresh OIDC token ("Pushing signature to: acrplatformi3aldz.azurecr.io/apps/workorders/<repo>").
  `supply_chain` attested an SBOM and the step-authored provenance for each digest (Rekor entries 2963918295,
  2963918448, 2963920174, 2963920483, 2963921171, 2963921423) and ended `done: 3 image(s)`. Digests:
  `ui-server` `sha256:daaa5387c2d1b616f76efa0b9f99b4dfcb84125b89dc7ff30549d96daea648c6`, `worker`
  `sha256:d9779ed2bf8c1c3bac84b67cbc3e32a636bec9d15906d1bf319470c230dd2cca`, `db-migrator`
  `sha256:fed9f72e381ac5cd712d5610e7a34ddbbefd1f1d34b4fac3013dd0a11165f8d1`. Pass from the step logs; the
  registry-side `cosign verify` of [supply-chain-evidence.md](supply-chain-evidence.md) was not repeated for 2.5.723.
- **Tags locked.** `supply_chain` locked `2.5.723` and `sha-e3db0f4` of each of the three repositories
  (`locking apps/workorders/<repo>:<tag>`, step `success`). Pass from the step log; not re-read from the ACR API.
- **Green streak: 1 of 10.** The master `workorders/ci` build `6ab7072c718b2dc51632cc3c` (b15efaa, 2026-09-25
  23:43Z, run by hand as a push event, `webhookTriggered: false`) ended `error` in `acceptance` (the SQL Server on
  1434, section above), which resets the streak of 5; this build is 1. Rows in
  [supply-chain-evidence.md](supply-chain-evidence.md#green-streak-2026-09-26-1-of-10).

