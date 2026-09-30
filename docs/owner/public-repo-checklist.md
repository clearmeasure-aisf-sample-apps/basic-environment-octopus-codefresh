# Owner checklist: the environment repository is public (#47)

Owner: the organization owner (`clearmeasure-aisf-sample-apps` admin). **Owner-only. The feature loop, its sub-sessions and
CI never run these steps**: they change repository settings, security settings and credentials, which no automated
session may do. Design: [platform-design.md §6.2, "Public repository (#47)"](../../design/platform-design.md#public-repository-47).

The repository became public on 2026-09-30. Everything in it and its full history is world-readable, and a push ruleset that
restricts file paths cannot exist on a public repository (E31). What protects `main` is therefore the branch ruleset, and
what protects the secrets is that none is ever committed. Two things are still open.

## Live state versus target

Read-only check of 2026-09-29 (repeat it with the commands under "Verify"):

| Setting | Live | Target |
|---|---|---|
| Secret scanning | disabled | enabled |
| Push protection | disabled | enabled |
| Push ruleset (file paths) | none; not possible on a public repo | none (replaced by the rows below) |
| `main-protection` (id 24154011): deletion, non-fast-forward | blocked | blocked |
| `main-protection`: required approving reviews | 0 | 1 or more |
| `main-protection`: code-owner review | not required | required (CODEOWNERS) |
| `main-protection`: required status | `codefresh/env-checks`, non-strict | unchanged |
| `main-protection`: bypass actors | repository role 5 (Admin), always | the Octopus GitHub App only |

## Steps

Do them in this order: 1 and 2 first, because history is already public.

### 1. Enable secret scanning and push protection

1. Repository → Settings → Advanced Security (Code security): enable **Secret scanning**, then **Push protection**.
2. Review every alert that appears (Security → Secret scanning). Each alert is a leaked credential: rotate it (step 2c), then
   close the alert with the matching resolution.
3. Dependabot security updates are also disabled today; enable them if you want the alerts (optional, not part of #47).

### 2. Scan the whole history and rotate what is found

The history has 469 commits (2026-09-29). Secret scanning covers known partner patterns only, so scan the full history yourself
with [TruffleHog](https://github.com/trufflesecurity/trufflehog).

a. Scan every commit of every branch and pull request ref. Write the output outside the repository, never into it:

   ```bash
   git clone --mirror https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git env-history.git
   trufflehog git file://$PWD/env-history.git --no-update --results=verified,unknown --json > ~/trufflehog-env-history.json
   ```

b. Read the findings. A finding is real when the value is a live credential, or was live at any time. Treat "unknown"
   (could not be verified) as real until you prove otherwise, because a verifier only says whether it is live now.
c. **Rotate everything found, whether or not it is still live**, using [credential-rotation.md](../runbooks/credential-rotation.md),
   including the **old GitHub PAT** (section 2 there: the org token behind the Octopus Git credential, the Codefresh Git
   integration, the `platform-conformance` context and the interim Argo CD repository credential). Then revoke the old value at
   its source. Rewriting history does not help: clones and forks already hold it.
d. Record the outcome (date, tool version, number of findings, what was rotated) as a comment on issue #47. Never paste a secret
   value there.

The offline check `NoSecretsInTrackedFiles` and gitleaks (CAP-KIT-007) cover the current tree on every push; they do not cover
history.

### 3. Tighten `main-protection` to the target

Repository → Settings → Rules → Rulesets → `main-protection` (id 24154011):

1. **Require a pull request**: required approving reviews **1**, **Require review from Code Owners** on, dismiss stale approvals
   on new commits on. Keep the required status check `codefresh/env-checks`.
2. **Bypass list**: remove repository role **Admin**; add the **Octopus GitHub App** (the identity that writes the pin commits,
   installed on this repository only, permission *Contents: Read and write*), mode *always*.
3. Leave *Restrict deletions* and *Block force pushes* on.

Consequences to plan for before you save:
- With one operator, a required approval needs a second account: GitHub does not count the author's own approval. Add a
  reviewer account that is a member of `platform-owners`, or the operator cannot merge platform changes. Once the Admin bypass
  is removed there is no self-merge; a break-glass change means adding yourself to the bypass list for the duration of the change
  and removing yourself afterwards ([break-glass.md](../runbooks/break-glass.md)).
- [VERIFY] Octopus's Git credential takes a user name and a token, and a GitHub App installation token expires after one hour.
  The project Git settings now use the Octopus GitHub App connection (#42, [octopus-github-app-git.md](../runbooks/octopus-github-app-git.md)),
  but the Argo CD image-tag step still picks a stored Git credential by repository restriction (issue #58), so the machine
  user of that credential stays on the bypass list next to the App until #58 lands. Before removing the machine user's path,
  prove that the pin commit works with the App (a step that mints the token, or a supported credential type). If it does
  not, the fallback is the team-based bypass of the earlier design (team `platform-bots`
  holding the machine user); that is a weaker target than the App, and the design section 6.2 must be amended when you choose it.
- Pin commits keep working while the Admin bypass is still on; do this step last and test one deployment to `tdd` afterwards
  (the image-tag step must commit the pin, and the task log must show no bypass or rule-violation error; the exact check is in
  [octopus-github-app-git.md](../runbooks/octopus-github-app-git.md)).

## Related: the board credential (#45)

The board workflow's credential is moving to the `aisf-board` GitHub App (work item #45); an owner-only session is creating that
App. Until the Actions secrets `BOARD_APP_ID` and `BOARD_APP_PRIVATE_KEY` exist, `PROJECTS_PAT` is the fallback, so rotate it after the
history scan if the scan finds it. In this public repository the workflow's safeguards are: it listens to `pull_request`, never
`pull_request_target`, and runs only for same-repository pull requests; it checks out no pull request code, reads the title and body
only as event-file data (never expanded into a script), and it is the only workflow that reads secrets (offline check TB24).

## Verify (read-only; safe for anyone with access)

```bash
R=clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh
gh api repos/$R --jq '{private, visibility, secret_scanning: .security_and_analysis.secret_scanning.status, push_protection: .security_and_analysis.secret_scanning_push_protection.status}'
gh api repos/$R/rulesets --jq '.[] | {id, name, target, enforcement}'
gh api repos/$R/rulesets/24154011 --jq '{bypass_actors, rules: [.rules[] | {type, parameters}]}'
gh api repos/$R/secret-scanning/alerts --jq 'length'
```

Expected when done: `visibility` is `public`; both statuses are `enabled`; only `main-protection` (branch target) exists, with no
push ruleset; `pull_request` has `required_approving_review_count` of 1 or more and `require_code_owner_review` true; `bypass_actors`
lists only the Octopus GitHub App (`actor_type` `Integration`); the alert count is 0 or every alert is resolved after rotation.
Then update the "Live versus target" table above and the last paragraph of design section 6.2 in a pull request.
