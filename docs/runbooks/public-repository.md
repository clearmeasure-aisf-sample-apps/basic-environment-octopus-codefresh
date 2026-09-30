# Public repository

`clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` has been public since 2026-09-30. Everyone can read every file and every commit, fork the repository and open pull requests. This page lists what that changes and the steps that only the organization owner can take. Claude sessions and pipelines never perform them: each is a GitHub repository setting (design R36).

Contracts: design §6.2 (enforcing the write matrix), ADR-C5, ADR-IR14, ADR-IR15, R3, R36, threat model in §13, E31; tests `PublicRepositoryTests` (CAP-KIT-007, CAP-KIT-006), `PinWriterTests` (CAP-OCT-012), `SecretScanTests` (CAP-KIT-007).

## What replaced the private-repo controls

| Was (private repo) | Is (public repo) |
|---|---|
| Push ruleset "restrict file paths": `.octopus/**` and `envs/**` writable only through merged pull requests or by a bot (E31: push rulesets exist only for private or internal repos) | Branch ruleset on `main`: pull request, code-owner approval and `codefresh/env-checks` required; the Octopus GitHub App is the only bypass actor |
| Path ownership by the ruleset | `CODEOWNERS` (platform and security owners) |
| Prevention of a bot writing outside the pin fields | Detection: the bot-path audit of `platform-env/env-checks` (`PinWriterTests`, CAP-OCT-012) fails when a commit by a bot identity changes anything but pins; the response is a reviewed revert and a credential review |
| Committed values are unreadable to outsiders | No secret or credential-shaped value in any tracked file (`PublicRepositoryTests`, gitleaks); no workflow on `pull_request_target` (`PublicRepositoryTests`, TB24) |

## Owner-only steps

None of these is done by repository changes. Do them in order and record the date in the issue that tracks the work (#47 of this repository).

1. **Create the branch ruleset on `main`** (Settings, Rules, Rulesets). Rules: require a pull request before merging; require review from code owners; require the status check `codefresh/env-checks`; block force pushes and deletion. Bypass list: the Octopus GitHub App only (R3). Until that App exists, the interim bypass is team `platform-bots` (the Octopus Git credential's machine user), removed when the App is installed. No push ruleset can be created on a public repository.
2. **Enable secret scanning and push protection** (Settings, Advanced Security). Both are available on public repositories.
3. **Scan the full history with TruffleHog**, all 469 commits present at the time of the switch, before trusting the history:

   ```powershell
   trufflehog git file://. --results=verified,unknown --no-update
   ```

   Run it from a fresh clone on the operator's machine, never in a pipeline. Treat every finding as disclosed: rotate it first ([credential-rotation.md](credential-rotation.md)), then decide whether the history needs rewriting. Rotate the old GitHub PAT whether or not the scan finds it.
4. **Review the committed identifiers.** `terraform/tier/*.tfvars` hold tenant, subscription and resource identifiers. They are not credentials, but they are now world-readable (ADR-IR14). Decide whether to accept that or move them to Octopus variables; the change is tracked as a child work item of #47.
5. **Decide the Argo CD repository credential.** A public repository needs none for Argo CD to read it, so the stored PAT and the planned read-only App (R11) may be unnecessary. `terraform/tier` still writes `argocd-repo-creds` while `argocd_repo_private` is `true`; retiring it is a separate work item.

## Checks in this repository

- `dotnet test tests/Platform.Conformance.Offline --filter FullyQualifiedName~PublicRepositoryTests` runs the credential-shape scan and the `pull_request_target` check offline, with no secret and no external tool.
- `pwsh -NoProfile -File scripts/checks/validate-all.ps1 secrets` runs gitleaks with `.gitleaks.toml` over the tree (the full scan; the offline scan is a backstop where gitleaks is missing).
