---
name: feature-loop
description: >
  Run the feature loop on ONE GitHub work item: drive it across the shared project board
  https://github.com/orgs/clearmeasure-aisf-sample-apps/projects/678 one column at a time
  (Todo = design, In Progress = implement, In Review = verify, Deployed to TDD/UAT/Prod =
  post-merge verification for the app repo, Done = terminal) through code, tests, a
  conflict-free PR, API-verified Codefresh/Octopus/Argo CD evidence, bot-finding triage,
  merge, and board moves. Bare `#N` means an issue in clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh; `owner/repo#N`
  targets the other repo on the board. Use when asked to "run the feature loop on work
  item #N", "work issue #N through the board", or when /feature-loop N is invoked. For a
  batch of items, use /feature-loop-dispatch instead.
---

# Feature Loop (single work item)

Drives exactly one work item end-to-end. Configuration (board, columns, per-repo gates, CI
and deployment signals, board-move transport) comes from `.claude/factory-loop.json` at
the repo root. These rules are the contract; a user's own global rules may add to but
never weaken them. The same skill and the same board serve both repositories:

| Repo | Kind | Default branch | Column path |
|---|---|---|---|
| `clearmeasure-aisf-sample-apps/20260923-001` | app (`workorders`) | `master` | Todo, In Progress, In Review, Deployed to TDD, Deployed to UAT, Deployed to Prod, Done |
| `clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh` | environment (`platform`) | `main` | Todo, In Progress, In Review, Done |

Target resolution: `#N` is an issue in `defaultRepo` (clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh); `owner/repo#N`
names the repo explicitly and must be a key of `repos` in `factory-loop.json`. Work on the
other repo happens in a fresh clone of it, under that repo's own `CLAUDE.md` rules. The
optional project field `App` (`workorders` / `sandbox` / `platform`) is set by the board
workflow when it adds an item (from the environment repo's `apps/*.yaml` descriptors);
never set it by hand.

## Columns, phases and evidence

| Column | Phase | The item is here while | Leaves when (evidence) |
|---|---|---|---|
| Todo | design | the design is written | a design comment on the issue (below) exists; the move to In Progress is the start of implementation |
| In Progress | implement | code and tests are written and the local gates run | the PR is opened (then In Review) |
| In Review | verify | the PR is reviewed and verified | app: `codefresh/ci` success on the PR head, bot findings triaged, PR merged, then the TDD deployment succeeded. environment: local gates passed and summarised in the PR, PR merged, and for `gitops/` changes Argo CD Synced/Healthy |
| Deployed to TDD | verify (app only) | the release runs in tdd | the Octopus deployment of the release carrying the merge commit to `uat` succeeded |
| Deployed to UAT | verify (app only) | the release runs in uat | the deployment to `prod` succeeded |
| Deployed to Prod | verify (app only) | the release runs in prod | the issue is closed with the evidence comment (then Done) |
| Done | terminal | - | - |

**Closing is the terminal move.** Every closed issue ends in Done (for any close reason)
and a reopened issue goes back to Todo. So:

- Never close an issue (and never use a closing keyword such as `Closes #N` / `Fixes #N`
  in a PR body or commit message) before the item's last verification column is proven:
  Deployed to Prod for app items; merge (plus Argo CD Synced/Healthy for `gitops/`
  changes) for environment items. PRs reference the item with `Refs #N`
  (`prLinkKeyword` in `factory-loop.json`). A closing keyword would also let the board
  workflow move the issue to Done when the PR merges.
- The loop closes the issue itself, as its last action, with an evidence comment (PR,
  merge SHA, CI/release/deployment IDs or Argo CD state). That close is what makes the
  item Done.
- Closing an issue as not planned or duplicate also lands it in Done; do that only when
  the item is genuinely abandoned, and say so in the closing comment.
- A merged PR moves the PR card (not a `Refs` issue) to Done. The issue stays in its
  verification column until its own evidence exists.

## Moving cards

Cloud Claude Code sessions cannot call GitHub GraphQL, org-level endpoints or the Actions
API (the proxy refuses them). Cards therefore move through the board workflow
`.github/workflows/project-board.yml` of the environment repo (secret `PROJECTS_PAT`; the
environment repo's `docs/tool-boundaries.md`, "Board automation"). Its events fire only for
issues and PRs **of the environment repo** - GitHub Actions stays off in app repos:

1. **Environment-repo items: rely on the automatic events** and do not duplicate them:
   issue opened or reopened -> Todo; issue closed (any reason) -> Done; PR opened, reopened
   or ready for review -> the PR and every issue it *closes* to In Review; PR merged -> the
   same items to Done. With `Refs #N` the issue is not among them, so its In Review move is
   a dispatch.
2. **Everything else is a `board-status` dispatch to the environment repo**: for app-repo
   items every move (Todo when the issue or a child is created, In Progress, In Review for
   the issue and the PR, Deployed to TDD / UAT / Prod, Done after the close, Todo after a
   reopen); for environment-repo items In Progress and In Review of the issue; for both,
   every parent move back or forward. Dispatches are idempotent (an item already in that
   column, or already on the board, is fine), and a dispatch for an item not yet on the
   board adds it.

   ```bash
   repo=clearmeasure-aisf-sample-apps/20260923-001   # the item's repo
   n=123; column='In Progress'
   body=$(python3 -c 'import json,sys; print(json.dumps({"event_type":"board-status","client_payload":{"repository":sys.argv[1],"issue":int(sys.argv[2]),"status":sys.argv[3]}}))' "$repo" "$n" "$column")
   printf 'header = "Authorization: Bearer %s"\n' "$GH_TOKEN" | curl -sS --config - \
     -o /dev/null -w '%{http_code}\n' -X POST -H 'Accept: application/vnd.github+json' \
     -d "$body" https://api.github.com/repos/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh/dispatches
   ```

   `204` is the success signal (verified live from a cloud session; the token needs
   *Contents: write* on the environment repo, which `repository_dispatch` requires). The same move can be
   requested as a `workflow_dispatch` of `project-board.yml` with inputs `repository`,
   `issue`, `status` (locally with `gh workflow run`). The Actions API (`/actions/*`, run
   results) is refused by the proxy in cloud sessions, so do not poll workflow runs there.
   Cheap confirmation, when readable: `GET /repos/{o}/{r}/issues/{n}/timeline` shows an
   `added_to_project_v2` or `project_v2_item_status_changed` event after the request.
3. **If the dispatch is refused** (any answer other than `204`: 403/404 from the proxy or
   GitHub, a token without write access to the environment repo), post a fallback comment on the
   issue, exactly `board-status: <column> -- intended board column; the board automation
   could not be reached from this session, so the card needs a manual move.`, name the
   refused call and its HTTP status in the next status update, and continue. Never stop
   the loop because a card could not move.
4. **Locally, with `gh` authenticated with the `project` scope**, direct GraphQL is
   allowed. Resolve IDs at runtime (never hard-code them):

   ```bash
   gh api graphql -f query='query{organization(login:"clearmeasure-aisf-sample-apps"){projectV2(number:678){id fields(first:50){nodes{... on ProjectV2SingleSelectField{id name options{id name}}}}}}}'
   gh api graphql -f query='query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){issue(number:$n){id projectItems(first:20){nodes{id project{number}}}}}}' -F o=<owner> -F r=<repo> -F n=<N>
   gh api graphql -f query='mutation($p:ID!,$i:ID!,$f:ID!,$v:String!){updateProjectV2ItemFieldValue(input:{projectId:$p,itemId:$i,fieldId:$f,value:{singleSelectOptionId:$v}}){projectV2Item{id}}}' -F p=<projectId> -F i=<itemId> -F f=<statusFieldId> -F v=<optionId>
   ```

   The item ID is the ProjectV2 item ID (project number 678 in `projectItems`), not the
   issue node ID; an issue not yet on the board is added with `addProjectV2ItemById`.

Record every move you request as a one-line issue comment `board-status: <column> (via
dispatch | graphql | fallback)`, folded into the column's deliverable comment where one is
posted anyway. In a cloud session those markers are the readable board history.

**Reading a card's column.** Locally: GraphQL (`fieldValueByName(name:"Status")` on the
item). In a cloud session, derive it from REST facts, rightmost match wins: issue closed ->
Done; latest `board-status:` marker; for app items a successful Octopus deployment of a
release carrying the merge commit -> that environment's column; a PR with `Refs #N` open
or merged -> In Review; a branch or draft PR for #N -> In Progress; otherwise Todo.

## Children first, depth-first (before touching the item)

Resolve `GET /repos/{owner}/{repo}/issues/{N}/sub_issues` BEFORE touching #N (sub-issues
may live in the other repo; each carries its own repository). Recurse to the deepest open
descendant and work the tree bottom-up: a child is driven to Done, then its parent is
reconsidered. A parent is never started while any of its descendants are open.
Independent siblings may be worked in parallel, each in its own worktree. Report the
resolved tree and execution order before starting.

## Column progression

- **One column at a time.** The item advances exactly one column of its repo's
  `columnPath` per transition, never skips, and only after that column's evidence is
  verified.
- **Independent subagent per column.** Each column's work is done by its own dedicated
  subagent; one column's deliverable is the next column's input. No single subagent
  carries the item through multiple columns.
- **Todo (design) deliverable:** one issue comment with the problem, the approach, the
  acceptance criteria, the test plan per layer and the risks. App items also name the
  Onion layers touched. Environment items also name the paths touched, the
  `validate-all.ps1` sub-commands they need (`checksByPath`), the tool-boundary rules
  (`docs/tool-boundaries.md`, TB01-TB24) at stake, and whether `gitops/` changes (which
  adds the Argo CD check). Then dispatch In Progress.
- **In Progress (implement):** branch `{username}/{branch-description}` from the default
  branch, code and tests, the repo's gates (below), merge the default branch in, push,
  open the PR with `Refs #N`, then dispatch In Review for the issue (for an app PR, for
  the PR too; an environment-repo PR moves itself).
- **In Review (verify):** the PR checks (below), bot triage, merge. App items stay here
  until the TDD deployment succeeds; environment items end here and are closed.
- **Deployed to TDD / UAT / Prod (app only):** one dispatch per environment, each only
  after that environment's deployment task succeeded; then close the issue from Deployed
  to Prod.
- Non-applicable columns are passed through with a recorded no-op justification (an issue
  comment stating why the column does not apply), still one column at a time. Example: a
  docs-only app change still ships through a release, so the deployment columns apply.

## Subagent rules

- **One git worktree per writing subagent.** Any subagent that edits files, builds, or
  runs tests gets its own git worktree (`isolation: "worktree"`). Parallel subagents never
  share a checkout. Read-only search agents may use the main checkout.
- **All subagents run on Sonnet** (`model: "sonnet"`, `subagents` in `factory-loop.json`);
  no stage is downgraded to Haiku.

## Gates: app repo (`clearmeasure-aisf-sample-apps/20260923-001`)

- **Private build before commit:** `pwsh -NoProfile ./PrivateBuild.ps1` must pass.
- **Acceptance tests before PR:** `pwsh -NoProfile ./AcceptanceTests.ps1`.
  `ACCEPTANCE_SKIP_CLEAN=true` skips `dotnet clean` and builds incrementally when this
  checkout already holds a Release build (use it right after a passing private build);
  `PLAYWRIGHT_TRACE` is `retain-on-failure` by default, `on` or `on-first-retry` when a
  failure needs traces.
- **Merge master before PR (mandatory):** fetch and merge `origin/master` into the branch,
  resolve conflicts, re-run the private build. PRs arrive conflict-free.
- **Repository rules (from the app's `CLAUDE.md`, auto-rejected in review):** strict Onion
  dependencies (Core references nothing; DataAccess only Core); no new NuGet packages or
  SDK changes without approval; no edits to `.octopus/`, build scripts or pipeline files
  without approval; NUnit 4 + Shouldly (no FluentAssertions, no `Assert.That`); test
  doubles prefixed `Stub`; AAA without section comments; test names
  `[MethodName]_[Scenario]_[ExpectedResult]` prefixed `Should` or `When`; live-LLM tests
  use `[LlmTest]`; file-scoped namespaces; DbUp scripts numbered sequentially with tab
  indentation; Qodana baseline refreshed only from a real scan.
- **Testing policy (definition of done):** automated tests in the same PR at every
  applicable layer: unit (`src/UnitTests`, bUnit for components, AutoBogus data),
  integration (`src/IntegrationTests`) wherever a module boundary is crossed, full-system
  (`src/AcceptanceTests`, Playwright driving the real UI, third parties stubbed). A layer
  that does not apply is explained in the PR description. UI features MUST have a
  Playwright test that drives that UI.

## Gates: environment repo (`clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh`)

- **Offline tests:** `dotnet test tests/Platform.Conformance.Offline` (the SessionStart
  hook installs the SDK; on a bare host set `DOTNET_ROOT` and `PATH` to the installed
  SDK). The consistency failure C09 is pre-existing (`knownFailures`); any other failure,
  or a new one, blocks the PR.
- **Checks:** `pwsh -NoProfile -File scripts/checks/validate-all.ps1 <checks>` with the
  sub-commands from `checksByPath` (always `powershell boundaries consistency secrets`,
  plus the ones for the paths touched). A missing local tool is a `SKIP`, reported in the
  PR description, never silently dropped.
- **Repository rules:** one verb per tool and the TB01-TB24 lanes
  (`docs/tool-boundaries.md`); PowerShell 7 with the preamble of `docs/scripting.md` for
  every script; secrets never on a command line; platform files outside app-scoped paths
  name no app (TB22); nonprod-only changes unless the owner approved a prod change.
- **Pull request, not a direct push.** People push small changes straight to `main`, but a
  work item always goes through a PR so the board moves. Merge `origin/main` into the
  branch and re-run the gates before pushing.
- **No PR CI.** The `codefresh/env-checks` trigger is paused, so there is no status to
  wait for; the PR description carries the gate summary (tests passed/failed with the
  known C09, each check's PASS/FAIL/SKIP). If branch protection refuses the merge (a
  required status or CODEOWNERS review), report `STATUS: BLOCKED` with the exact refusal;
  never bypass it.

## PR, CI and merge (both repos)

- **CI is API-verified only.** Never report a PR complete on "PR created", a local build,
  or a shell exit code.
- **App PR head:** `GET /repos/{owner}/{repo}/commits/{head_sha}/status` must list context
  `codefresh/ci` with `state: success`. `pending` longer than 60 minutes is stuck
  (re-push or ask the operator to re-run the Codefresh build); `failure` / `error` is
  fixed and re-pushed.

  ```bash
  printf 'header = "Authorization: Bearer %s"\n' "$GH_TOKEN" | curl -sS --config - \
    "https://api.github.com/repos/$repo/commits/$sha/status" \
    | python3 -c 'import json,sys; d=json.load(sys.stdin); print([(s["context"], s["state"], s["updated_at"]) for s in d["statuses"]])'
  ```

- **Triage every bot review finding before merge:** list review comments from
  static-analysis bots (`github-code-quality[bot]`, `github-advanced-security[bot]`,
  Qodana, etc.). Each is fixed in the same PR or declined with a one-line reply on the PR.
- **Merge** with the repo's `mergeMethod` (`PUT /repos/{o}/{r}/pulls/{n}/merge` or the
  GitHub MCP merge tool) and record the merge commit SHA.

## Post-merge verification

**App repo (`workorders`, lifecycle `platform-continuous`, which deploys tdd -> uat -> prod
automatically):**

1. `codefresh/release` is `success` on the merge commit (same status API).
2. Find the Octopus release that carries the merge commit. The Octopus key is `$OCTOPUS`,
   sent only as a header through standard input, never printed or placed on a command
   line:

   ```bash
   oct() { printf 'header = "X-Octopus-ApiKey: %s"\n' "$OCTOPUS" | curl -fsS --config - "https://clearmeasure.octopus.app/api/Spaces-335/$1"; }
   oct projects/all                                  # Id of slug "workorders"
   oct "projects/<projectId>/releases?take=30"       # ReleaseNotes line "app-commit: <sha>"
   oct environments/all                              # Ids of tdd, uat, prod
   oct "deployments?projects=<projectId>&environments=<envId>&take=10"
   oct "tasks/<TaskId>"                              # State, HasPendingInterruptions
   ```

   The carrying release is the one whose `app-commit` equals the merge SHA, or failing that
   the oldest later release whose `app-commit` contains it
   (`GET /repos/{o}/{r}/compare/{merge_sha}...{app_commit}` -> `status` `ahead` or
   `identical`). Name that release version in every evidence comment.
3. For tdd, then uat, then prod: a deployment of the carrying (or a later containing)
   release whose task `State` is `Success` -> dispatch that environment's column. `Failed`,
   `Canceled` or `TimedOut` -> file a child defect with the task ID and stop the item.
   `HasPendingInterruptions`, a `prod-weekend-freeze`, or `Queued`/`Executing` past 90
   minutes -> `STATUS: BLOCKED` with the task ID. Never answer manual interventions,
   override a freeze, or start runbooks to get past it.
4. After Deployed to Prod: close the issue with the evidence comment (PR, merge SHA,
   `codefresh/ci` and `codefresh/release` states, release version, the three deployment
   and task IDs), then dispatch `Done` (app-repo issues have no automatic close event).

**Environment repo:**

1. Confirm the merge commit is on `origin/main`.
2. If the PR changed `gitops/`: after Argo CD has had a few minutes to reconcile, every
   Application rendered from the changed paths reports sync `Synced` and health `Healthy`
   (`kubectl get applications.argoproj.io -n argocd -o json` on the tier's cluster with the
   read access this repo's operating rules grant; prod is read-only). A tier that is asleep
   is not woken outside `env-wake` (TB17); if the check cannot run, the item stays In Review
   and the status is `STATUS: BLOCKED` with the reason.
3. Close the issue with the evidence comment (PR, merge SHA, gate summary, Argo CD
   Applications and their sync/health). The close moves it to Done automatically.

## Discovered work becomes a child

New work found while working #N (a follow-up, a defect, a deferred bot finding) becomes a
**child sub-issue** of #N, never a free-floating sibling. An environment-repo issue lands
in Todo when it is opened; an app-repo issue is put on the board with a `Todo` dispatch.

```bash
# child_id is the child's numeric "id" from GET /repos/{o}/{r}/issues/{child}, NOT its number
POST /repos/{o}/{r}/issues/{N}/sub_issues   {"sub_issue_id": <child_id>}
GET  /repos/{o}/{r}/issues/{N}/sub_issues   # verify
```

(The GitHub MCP sub-issue tool does the same.)

## Parent clamp

A parent's column may never be further right (in `columnOrder`) than the least-advanced of
its open children: `parent = min(intended, min(column of each open child))`. A parent is
never closed while any child is open; filing a child from a late column pulls the parent
back to the child's column (reopen it if it was closed: an environment-repo reopen lands
in Todo by itself, an app-repo reopen needs a `Todo` dispatch; then dispatch the clamp
column if that is further right). Record each clamp as a comment on the
parent naming the child that caused it; the parent advances again only as its children
advance. Parent moves use the same dispatch as any other move.

## GitHub API access and budget

- **Local:** `gh` (REST for reads, comments, PRs; GraphQL only for board mutations).
- **Cloud session:** the GitHub MCP tools, or `curl` against `https://api.github.com` with
  `$GH_TOKEN` passed as a header through `--config -` on standard input. GraphQL and org
  endpoints are refused by the proxy; do not retry them.
- **Secrets:** `$GH_TOKEN`, `$OCTOPUS` and `$CODEFRESH` are never echoed, logged, written
  to files in the repo, or placed on a command line.
- Check `GET /rate_limit` before any fan-out.

## Completion heartbeat (mandatory - never hand off while pending)

The agent driving #N MUST NOT end a turn or send a final message while CI, merge, a
deployment, the issue close, or a card move is still pending. Poll every 60-90 seconds:
the commit status of the PR head, then of the merge commit, then the Octopus deployment
tasks of tdd, uat and prod. Forbidden final states: "CI still running", "in progress",
"will follow up", "monitoring". Every wait has a deadline (CI 60 minutes, each deployment
90 minutes, no observable progress 20 minutes); past it, check state directly and either
act or report `STATUS: BLOCKED`.

The final message MUST begin with **`STATUS: COMPLETE`** or **`STATUS: BLOCKED`**, then:
final board column, PR number, merge commit SHA, the CI/release/deployment (or Argo CD)
evidence, any card moves that fell back to a comment, and any children created.

## Reporting

Every status update states, in order: what happened, what it means for the work item, and
what happens next, in plain software-team vocabulary (work item, defect, pull request,
build, automated tests, release, deployment, board status), no orchestration jargon.
