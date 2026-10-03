# Operator identity: one-time setup

The demo-environment skill runs its operator phases (0 to 4, progression, teardown) from a shell. This page sets that shell up once, so the AI sessions that run the skill work under an identity of their own and never under a person's login. A person does the steps below once, because no identity can create or empower itself. After that, nothing needs a password, an MFA prompt or a manual renewal.

| System | Operator identity | Credential | Where it lives |
|---|---|---|---|
| This machine | Linux user `aiops` | — | The AI sessions run as this user (`sudo -iu aiops`, then `claude`) |
| Azure | Service principal `cm-ai-ops`: Owner of the demo subscription; owns its own app registration with Graph `Application.ReadWrite.OwnedBy` | Client secret, renewed by the identity itself (`update-operator-secret.ps1`, weekly timer) | `az`'s profile, `~aiops/.azure` |
| Octopus | Service account `ai-ops` in a team with the role System Manager | API key without expiry | `~aiops/.config/demo-environment/octopus-api-key.cred`, encrypted by `systemd-creds --user` (only `aiops` on this host can decrypt it) |
| GitHub | Machine user, for example `cm-ai-ops-bot`, owner of the org | `gh` OAuth token (no expiry; GitHub revokes it after a year unused) | `~aiops/.config/gh/hosts.yml` (no keyring for a user without a desktop session) |

Service principals and service accounts are not subject to the MFA that Microsoft enforces for people signing in to Azure, which is why a person's `az login` cannot run unattended and this identity can.

## 1. Linux user (as the person, with sudo)

```bash
sudo useradd --create-home --shell /bin/bash aiops
sudo loginctl enable-linger aiops     # lets aiops's weekly renewal timer run without a login
sudo -iu aiops
```

As `aiops`: install mise (`curl https://mise.run | sh`, then `echo 'eval "$(~/.local/bin/mise activate bash)"' >> ~/.bashrc` and a new shell), then the tools, uv before azure-cli:

```bash
mise use -g uv@latest pwsh@7 gh@latest terraform@latest && mise use -g azure-cli@latest
```

Install Claude Code for `aiops` and sign it in with the account the AI sessions should use.

## 2. GitHub (the person in the browser, then `aiops`)

1. Sign out of GitHub (or use a private window) and create the machine user. Its email can be a plus address of the person's, for example `jeffrey+ai-ops@clear-measure.com`; GitHub's notifications for it then arrive in that inbox. Turn on 2FA and keep its recovery codes with the person.
2. As an org owner, invite it as an owner: `gh api --method PUT orgs/<org>/memberships/<machine-user> -f role=admin`. Accept the invitation as the machine user.
3. As `aiops`, sign `gh` in as the machine user (the browser step is the machine user's), then clone this repository (the skill's branch until it is merged):

```bash
gh auth login --web --scopes admin:org,repo,workflow,project,delete_repo
gh auth setup-git
gh repo clone <org>/basic-environment-octopus-codefresh ~/basic-environment-octopus-codefresh -- --branch claude/devops-demo-prerequisites-f91iid
```

## 3. Azure (as `aiops`, with a person's login once)

The person signs in inside `aiops`'s shell. They need Owner of the subscription and an Entra role that may grant application permissions (Privileged Role Administrator or Global Administrator) in its tenant. The script signs that login out when it is done.

```bash
az login
pwsh -NoProfile -File ~/basic-environment-octopus-codefresh/.claude/skills/demo-environment/scripts/new-operator-identity.ps1 -SubscriptionId <subscription-id>
```

It ends with the service principal replacing its first secret itself: a `PASS` there proves the self-renewal.

Weekly renewal timer, as `aiops`:

```bash
mkdir -p ~/.config/systemd/user
printf '[Unit]\nDescription=Renew the operator identity secret\n\n[Service]\nType=oneshot\nEnvironment=PATH=%%h/.local/share/mise/shims:/usr/bin\nExecStart=%%h/.local/share/mise/shims/pwsh -NoProfile -File %%h/basic-environment-octopus-codefresh/.claude/skills/demo-environment/scripts/update-operator-secret.ps1\n' > ~/.config/systemd/user/operator-secret.service
printf '[Unit]\nDescription=Weekly renewal check of the operator identity secret\n\n[Timer]\nOnCalendar=weekly\nPersistent=true\n\n[Install]\nWantedBy=timers.target\n' > ~/.config/systemd/user/operator-secret.timer
systemctl --user daemon-reload && systemctl --user enable --now operator-secret.timer
```

The script renews when the newest secret has 45 days or less left, so a missed week or two does no harm. `journalctl --user -u operator-secret` shows each run.

## 4. Octopus (the person in the browser, then `aiops`)

1. As an Octopus administrator: **Configuration → Users → Create service account**, username `ai-ops`.
2. On that account: **API Keys → New API key**, purpose `demo-environment operator`, expiry **Never**. Copy the key; Octopus shows it once.
3. **Configuration → Teams → Octopus Managers** (role System Manager: creates spaces and service accounts) → add `ai-ops` as a member.
4. As `aiops`, paste the key at the hidden prompt:

```bash
pwsh -NoProfile -File ~/basic-environment-octopus-codefresh/.claude/skills/demo-environment/scripts/set-operator-octopus-key.ps1 -OctopusUrl https://<instance>.octopus.app
```

It ends with `PASS ai-ops ... with SpaceCreate and UserEdit`. Phase 1 makes `ai-ops` a Space Manager of every space it creates, so status and promotion work there.

## 5. Check

As `aiops`, with a demo file (`SKILL.md`, "Inputs"):

```bash
pwsh -NoProfile -File ~/basic-environment-octopus-codefresh/.claude/skills/demo-environment/scripts/test-demo-prerequisites.ps1 -Config ~/demos/<slug>.json
```

Every line `PASS`, with Azure showing `servicePrincipal` and no warning about a person's login.

## Undo

Remove the Owner role assignment of `cm-ai-ops` on the subscription, then delete the app registration, the Octopus service account `ai-ops`, the GitHub machine user (or remove it from the org), and the Linux user (`sudo loginctl disable-linger aiops && sudo userdel --remove aiops`).
