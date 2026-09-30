# Public repository

`clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` has been public since 2026-09-30. Everyone can read every file and every commit, fork the repository and open pull requests. This page lists what changed and where the steps only the organization owner can take are kept. Claude sessions and pipelines never perform them: each is a GitHub repository setting.

Contracts: design section 6.2 (enforcing the write matrix), section "Public repository (#47)", ADR-C5, ADR-IR14, ADR-IR15, R2, E31; tests `NoSecretsInTrackedFilesTests` (CAP-KIT-007), `CommittedTfvarsGuardTests` (CAP-KIT-007), `GitHubWorkflowRuleTests` (TB24, CAP-KIT-006), `PinWriterTests` (CAP-OCT-012).

## What replaced the private-repo controls

| Was (private repo) | Is (public repo) |
|---|---|
| Push ruleset "restrict file paths": `.octopus/**` and `envs/**` writable only through merged pull requests or by a bot (E31: push rulesets exist only for private or internal repos) | Target: branch ruleset on `main` with a required pull request, code-owner approval and `codefresh/env-checks`; the Octopus GitHub App as the only bypass actor. Live today: pull request (0 approvals), `codefresh/env-checks`, Admin-role bypass. The difference is in [the owner checklist](../owner/public-repo-checklist.md) |
| Path ownership by the ruleset | `CODEOWNERS` (platform and security owners) |
| Prevention of a bot writing outside the pin fields | Detection: the bot-path audit of `platform-env/env-checks` (`PinWriterTests`, CAP-OCT-012) fails when a commit by a bot identity changes anything but pins; the response is a reviewed revert and a credential review |
| Committed values are unreadable to outsiders | No credential-shaped value in any tracked file (`NoSecretsInTrackedFilesTests`, gitleaks); no `pull_request_target` workflow, and `pull_request` only for same-repository pull requests (TB24) |

## Owner-only steps

Documented, never executed by a repository change: [docs/owner/public-repo-checklist.md](../owner/public-repo-checklist.md). In order:

1. Enable secret scanning and push protection.
2. Scan the full history with TruffleHog from a fresh clone on the operator's machine, never in a pipeline; treat every finding as disclosed and rotate it first ([credential-rotation.md](credential-rotation.md)); rotate the old GitHub PAT whether or not the scan finds it.
3. Tighten the `main-protection` ruleset to the target (required approvals, code-owner review, the Octopus GitHub App as the only bypass actor).
4. Committed identifiers (`terraform/tier/*.tfvars`): world-readable, not credentials. The owner accepted the exposure (#51, ADR-IR14): tenant and subscription IDs, CIDRs, resource and vault names and chart versions stay committed. Operator IP ranges and e-mail addresses never do: they stay empty and, if ever used, go through Octopus variables `TF_VAR_*`; the offline guard `CommittedTfvarsGuardTests` (CAP-KIT-007) fails otherwise.
5. Argo CD repository credential: none. A public repository needs none to be read, so R11 is retired and the credential wiring is removed (ADR-IR15, work item #41). The owner removes the lingering `argocd/argocd-repo-creds` Secret before revoking the PAT (credential-rotation.md, section 8).

## Checks in this repository

- `dotnet test tests/Platform.Conformance.Offline --filter "FullyQualifiedName~NoSecretsInTrackedFiles|FullyQualifiedName~CommittedTfvarsGuard|FullyQualifiedName~GitHubWorkflowRule"` runs them offline, with no secret and no external tool.
- `pwsh -NoProfile -File scripts/checks/validate-all.ps1 secrets` runs gitleaks with `.gitleaks.toml` over the tree (the full scan; the offline scan is a backstop where gitleaks is missing).
