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

Measured after: not yet. The first `workorders/release` after these changes (not before 02:00Z 2026-09-26, when the
app clusters may wake again) is the first measurement; record its build id and the table from Method here.

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
