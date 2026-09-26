# Supply-chain and release evidence for app #1

The app #1 clauses of the P1 exit criteria other than build duration (design §9; [cutover-and-decommission.md](../cutover-and-decommission.md)
criteria 8 and 9; build duration is [build-duration.md](build-duration.md)):

- images are signed, locked and verifiable with `cosign verify` against the app's release identity (CAP-CF-006, CAP-CF-007);
- each release is created exactly once, and a rerun is a no-op;
- 10 consecutive master builds pass every gate.

This runbook gives the read-only method and the readings of 2026-09-25 (21:30Z to 22:00Z), for release 2.5.722
(`workorders/release` build `6ab689102379153f0a5f78eb`, master `b15efaa`). Nothing below starts a build, pushes,
signs, changes a tag or calls a write endpoint of Octopus.

## Method

Credentials stay in files created under `umask 077`, are never on a command line and never printed, and each file is
deleted by its literal path after use.

### Tools

cosign from its GitHub release, checked against the release's checksum file before install:

```sh
V=v3.1.3   # latest tag of sigstore/cosign on 2026-09-25 (git ls-remote --tags)
curl -sSfLO "https://github.com/sigstore/cosign/releases/download/$V/cosign-linux-amd64"
curl -sSfLO "https://github.com/sigstore/cosign/releases/download/$V/cosign_checksums.txt"
grep ' cosign-linux-amd64$' cosign_checksums.txt | sha256sum -c -   # cosign-linux-amd64: OK
install -m 0755 cosign-linux-amd64 ~/.local/bin/cosign
```

Reading of 2026-09-25: cosign v3.1.3, SHA-256 `4629c757b7618056f8ddd7e2625ae9fdd94c0372a65049520bc7d9df9efc7f71`.

### Registry access (no `docker login`)

1. An AAD token of the provisioner service principal: client-credentials grant against
   `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token`, scope `https://management.azure.com/.default`,
   the form body written to a private file and sent with `curl --data-binary @file`.
2. An ACR refresh token: `POST https://acrplatformi3aldz.azurecr.io/oauth2/exchange` with
   `grant_type=access_token`, `service=acrplatformi3aldz.azurecr.io`, `tenant` and `access_token`, again from a file.
3. A private `DOCKER_CONFIG` directory whose `config.json` holds the refresh token as `identitytoken` for
   `acrplatformi3aldz.azurecr.io` (user `00000000-0000-0000-0000-000000000000`); cosign reads it.
4. For the ACR data plane (`/acr/v1/...`): `POST /oauth2/token` with `grant_type=refresh_token` and
   `scope=repository:apps/workorders/<repo>:metadata_read`, the bearer header in a private `curl -K` file.

### Signatures and attestations

The identity is the one the pipeline signs with (keyless, `cosign.sign: true` in
`codefresh/apps/workorders/pipelines/release.yml`) and the admission policy trusts
(`policies/kyverno/base/verify-release-signatures.yaml`, attestor `appReleasePipelines`, narrowed by the tenant chart to
the app's own pipeline): issuer `https://oidc.codefresh.io`, subject
`https://g.codefresh.io/clearmeasure/workorders/release:66327682d5f6e0bfd0ef936a/6ab48c904c7ffadba0ee23df`
(`6ab48c904c7ffadba0ee23df` is the id of pipeline `workorders/release`, `GET /api/pipelines/workorders%2Frelease`).

```sh
ID='https://g.codefresh.io/clearmeasure/workorders/release:66327682d5f6e0bfd0ef936a/6ab48c904c7ffadba0ee23df'
for r in ui-server worker db-migrator; do
  cosign verify --certificate-oidc-issuer https://oidc.codefresh.io --certificate-identity "$ID" \
    "acrplatformi3aldz.azurecr.io/apps/workorders/$r:2.5.722"
  for t in spdxjson slsaprovenance1; do
    cosign verify-attestation --new-bundle-format=false --type "$t" \
      --certificate-oidc-issuer https://oidc.codefresh.io --certificate-identity "$ID" \
      "acrplatformi3aldz.azurecr.io/apps/workorders/$r:2.5.722"
  done
done
```

The signatures are OCI referrers in the new bundle format (`https://sigstore.dev/cosign/sign/v1`); the SBOM and
provenance attestations are in the legacy `sha256-<digest>.att` tag, so cosign 3 needs `--new-bundle-format=false`
for them (`cosign tree` shows both). Without the flag `verify-attestation` fails with "none of the attestations
matched the predicate type".

### Tag lock

`GET https://acrplatformi3aldz.azurecr.io/acr/v1/apps/workorders/<repo>/_tags/<tag>` returns
`tag.changeableAttributes`; `GET .../_manifests/<digest>` returns the manifest's own `changeableAttributes`.

### Exactly-once release creation

- Octopus (`https://clearmeasure.octopus.app`, space `Spaces-335`, project `workorders` = `Projects-943`), read-only:
  `GET /api/Spaces-335/projects/Projects-943/releases?take=1000`, and
  `GET /api/Spaces-335/events?projects=Projects-943&take=500` filtered to `Created` and `Deleted` events whose
  `RelatedDocumentIds` hold a `Releases-*` id. `GET /events/<id>` gives `IpAddress` and `UserAgent`.
- Codefresh: `GET /api/workflow?pipeline=6ab48c904c7ffadba0ee23df&limit=100` (every `workorders/release` build),
  then the step statuses and logs of `image_reuse`, `supply_chain`, `supply_chain_reuse` and `octopus_release` from
  the progress JSON (method in [build-duration.md](build-duration.md#method)).

### Green streak

Every build of `workorders/release` and `workorders/ci` (`6ab48c8f4c7ffadba0ee23de`) with branch `master`, oldest
first, with the status of the build and of the steps `build_sql`, `acceptance`, `code_analysis`, `build_sqlite`,
`qodana`, `security_scan` and `gate`. Builds without a branch (manual runs) are listed where they failed.

## Readings of 2026-09-25

### Signatures (criterion 9): pass

| Image | Digest (tags `2.5.722` and `sha-b15efaa`) | `cosign verify` | SBOM (`spdxjson`) | Provenance (`slsaprovenance1`) |
|---|---|---|---|---|
| `apps/workorders/ui-server` | `sha256:c80ddf588ba5b865649b46780ab0076036b29a203373c96313ca2d27efd848cf` | pass | pass | pass |
| `apps/workorders/worker` | `sha256:76be7ff7511fbdaea724627285669d5eb72d7e1926f404fed7d6a7ba880b1fe3` | pass | pass | pass |
| `apps/workorders/db-migrator` | `sha256:5d49e696d59647f6ee3bf2d5fe4e505b5d112b65c6f5268be0b621c9f89203b3` | pass | pass | pass |

Each check reported the claims validated, the Rekor inclusion verified and the Fulcio certificate chain verified,
with the certificate subject and issuer above. The provenance names builder `https://g.codefresh.io/pipelines/workorders/release`
and invocation `https://g.codefresh.io/build/6ab689102379153f0a5f78eb`, with the image digest as its subject.

### Tag lock (criterion 9): pass for tags; manifests not locked

| Repository | `2.5.722` | `sha-b15efaa` | Manifest |
|---|---|---|---|
| `ui-server` | `writeEnabled: false`, `deleteEnabled: false` | `writeEnabled: false`, `deleteEnabled: false` | `writeEnabled: true`, `deleteEnabled: true` |
| `worker` | `writeEnabled: false`, `deleteEnabled: false` | `writeEnabled: false`, `deleteEnabled: false` | `writeEnabled: true`, `deleteEnabled: true` |
| `db-migrator` | `writeEnabled: false`, `deleteEnabled: false` | `writeEnabled: false`, `deleteEnabled: false` | `writeEnabled: true`, `deleteEnabled: true` |

The contract (CAP-CF-007, `TagLockTests`) is the tag lock, and it holds: `supply-chain.ps1` runs
`az acr repository update --image <repo>:<tag> --write-enabled false --delete-enabled false` for each tag. The
manifests themselves keep the defaults, so the lock is on the tags only. [VERIFY: whether ACR refuses
`az acr repository delete --image <repo>@<digest>` while a tag of that manifest is delete-locked. If it does not,
locking the manifest by digest as well (`--image <repo>@<digest>`) would close that path; not changed here.]

### Exactly-once release creation: one live release per version; one out-of-band recreate

Octopus holds two releases of `workorders`:

| Version | Release | Assembled | Release notes line 1 |
|---|---|---|---|
| 2.5.722 | `Releases-49479` | 2026-09-25T15:22:05Z | `app-commit: b15efaa3dbe51bfbb89d8dc4a93704e6882d7fae` |
| 2.5.721 | `Releases-49454` | 2026-09-24T19:32:01Z | `app-commit: 7606a1818b2d5ae87bac46e12c6140b925a20de9` |

Every `workorders/release` build and what it did to Octopus (the build node's egress is `20.118.80.199`):

| Build | Commit | Status | Images | `octopus_release` | Octopus events |
|---|---|---|---|---|---|
| `6ab54dc3fa2993efd6c19bbb` | 7606a18 (2.5.721) | error | built, signed, attested, locked | error: `Octopus API error: Sequence contains more than one element` | none |
| `6ab557885d4d1bff19f412cb` | 7606a18 | terminated in `build_sql` | — | — | none |
| `6ab55b395ee6342b4a12e1da`, `6ab55c2235db505c59d63530`, `6ab55f25d53dd099e34e1e9e` | none (manual) | YAML error, clone errors | — | — | none |
| `6ab5606db4147be81913cc3a` | 7606a18 | success | reused (locked) | not reached: the pipeline had no `supply_chain_reuse` yet | none |
| `6ab569db9cd4c6b62aa32422` | 7606a18 | success | reused; `supply_chain_reuse` success | created `Releases-49452` | `Events-1565284` Created 18:53:50Z from 20.118.80.199 |
| `6ab622ed332d50e0c6720158` | 7606a18 (rerun) | success | reused; `supply_chain_reuse` success | `--ignore-existing`: existing `Releases-49454` returned | only `BuildInformationModified` ×4 at 08:03:59Z; no `Created` |
| `6ab689102379153f0a5f78eb` | b15efaa (2.5.722) | success | built, signed, attested, locked | created `Releases-49479` | `Events-1568291` Created 15:22:06Z from 20.118.80.199 |

Between them, outside any build: at 19:28:18Z on 2026-09-24 the tdd deployment of `Releases-49452` failed; at
19:31:51Z an operator session (160.79.106.133, Python urllib) deleted its three tdd deployments and the release
(`Events-1565386`), and at 19:32:01Z created `Releases-49454` for 2.5.721 with the Octopus CLI from 160.79.106.139
(`Events-1565389`), which then deployed to tdd.

Verdict:

- **Pipeline side: pass.** Each version was created by exactly one pipeline build (2.5.721 by `6ab569db…`, 2.5.722
  by `6ab68910…`), and the one rerun of a released version (`6ab622ed…`) created nothing: it reused the locked images and
  `octopus release create --ignore-existing` returned the existing release.
- **Octopus side: 2.5.721 was created twice**, once by the pipeline and once by hand after a delete, so its live
  release (`Releases-49454`) did not come from a build. 2.5.722 is clean. For the criterion, count from 2.5.722 on,
  or record the 2026-09-24 delete and recreate as a pre-P1-exit repair.
- The Octopus CLI prints "Successfully created release version" also when `--ignore-existing` returns an existing
  release (build `6ab622ed…`, 08:04:06Z); the event log, not the step log, is the evidence of creation.

### Green streak: 5 consecutive green master builds, 5 of 10

Master builds, oldest first:

| # | Build | Pipeline | Commit | Build status | Gates (`build_sql`, `acceptance`, `code_analysis`, `build_sqlite`, `qodana`, `security_scan`, `gate`) |
|---|---|---|---|---|---|
| — | `6ab54dc3fa2993efd6c19bbb` | release | 7606a18 | error (`octopus_release`) | all success |
| — | `6ab557885d4d1bff19f412cb` | release | 7606a18 | terminated | `build_sql` terminated, the rest pending |
| — | `6ab55b395ee6342b4a12e1da`, `6ab55c2235db505c59d63530`, `6ab55f25d53dd099e34e1e9e` | release | none (manual) | terminated, error, error | not run |
| 1 | `6ab5606db4147be81913cc3a` | release | 7606a18 | success | all success |
| 2 | `6ab569db9cd4c6b62aa32422` | release | 7606a18 | success | all success |
| 3 | `6ab61cca1425991aeee74ac6` | ci | 7606a18 | success | all success |
| 4 | `6ab622ed332d50e0c6720158` | release | 7606a18 | success | all success |
| 5 | `6ab689102379153f0a5f78eb` | release | b15efaa | success | all success |

The streak starts after `6ab55f25…` (2026-09-24 17:34Z, failed in `main_clone`). The five green builds cover two
commits (7606a18 four times, b15efaa once), and every gate passed in each, the advisory `security_scan` included.
Five more green master builds are needed; they come from the next master pushes, not from reruns started for the count.

## Readings of 2026-09-26: release 2.5.723

`workorders/release` build `6ab7195012c13d5efe845c13` (master `e3db0f4`, the merge of `20260923-001` PR #5,
01:01:52Z to 01:25:26Z, `success`). Read from the build's step logs and the Octopus API; the registry-side
`cosign verify` and ACR lock readings above were not repeated for 2.5.723. Durations are in
[build-duration.md](build-duration.md#after-measured-2026-09-26).

### Signatures and tag lock: pass from the step logs

| Image | Digest (tags `2.5.723` and `sha-e3db0f4`) | Signed (image step) | SBOM, provenance (`supply_chain`, Rekor index) | Tags locked (`supply_chain`) |
|---|---|---|---|---|
| `apps/workorders/ui-server` | `sha256:daaa5387c2d1b616f76efa0b9f99b4dfcb84125b89dc7ff30549d96daea648c6` | yes | 2963918295, 2963918448 | both |
| `apps/workorders/worker` | `sha256:d9779ed2bf8c1c3bac84b67cbc3e32a636bec9d15906d1bf319470c230dd2cca` | yes | 2963920174, 2963920483 | both |
| `apps/workorders/db-migrator` | `sha256:fed9f72e381ac5cd712d5610e7a34ddbbefd1f1d34b4fac3013dd0a11165f8d1` | yes | 2963921171, 2963921423 | both |

`image_reuse` found none of the six tags (`image_reuse: build`, `IMAGES_REUSED=false`); each image step pushed both
tags, then signed with Cosign keyless (Codefresh OIDC, "Pushing signature to: acrplatformi3aldz.azurecr.io/apps/workorders/<repo>");
`supply_chain` attested and locked each digest's tags and ended `done: 3 image(s)`.

### Exactly-once release creation: pass

| Version | Release | Assembled | Release notes line 1 | Octopus `Created` events | `Deleted` events |
|---|---|---|---|---|---|
| 2.5.723 | `Releases-49489` | 2026-09-26T01:25:19Z | `app-commit: e3db0f4b7046564b072bdcd8522aa711deb2a1c5` | one: `Events-1569244` 01:25:19.4Z, 20.118.80.199 (build node egress), `octopus/2.26.0 (release;create)` | none |

The creation is the `octopus release create` of step `octopus_release` of build `6ab71950…` (log: `Successfully
created release version 2.5.723` at 01:25:19.6Z, `Releases-49489`). The next events are the tdd deployment
(`DeploymentQueued` 01:25:19.7Z, `DeploymentStarted` 01:25:20.8Z). No other build of `workorders/release` ran for
e3db0f4. Releases created by the pipeline since 2.5.722: 2.5.722 and 2.5.723, each once.

### Green streak (2026-09-26): 1 of 10

Master builds after row 5 of the 2026-09-25 table, oldest first:

| # | Build | Pipeline | Commit | Build status | Gates (`build_sql`, `acceptance`, `code_analysis`, `build_sqlite`, `qodana`, `security_scan`, `gate`) |
|---|---|---|---|---|---|
| — | `6ab7072c718b2dc51632cc3c` | ci | b15efaa | error (2026-09-25 23:43Z; run by hand as a push event, `webhookTriggered: false`) | `acceptance` error (its SQL Server on 1434 never became ready; [build-duration.md](build-duration.md#2026-09-26-acceptance-back-after-build_sql)), the rest success |
| 1 | `6ab7195012c13d5efe845c13` | release | e3db0f4 | success | all success (`qodana` beside the chains) |

`6ab7072c…` was the first run of the platform change that put `acceptance` beside `build_sql` on port 1434, rolled
back in `a850886`; the app commit b15efaa had passed every gate in `6ab68910…`. It is a red master build, so the
streak restarts after it: 1 consecutive green master build, 9 more needed. If the owner rules that a red master build
caused by a platform pipeline trial does not reset the streak, the count is 6 of 10; not assumed here.

## Repeat

Repeat the readings after each new master release: the signature, attestation and lock rows for the new VERSION,
the Octopus `Created` events against the release builds, and the next rows of the streak. A red master build resets
the streak to 0.
