#!/bin/bash
# SessionStart hook for Claude Code on the web: installs the toolchain the checks and the conformance suite need.
#   .NET SDK   per tests/global.json (rollForward latestFeature), into ~/.dotnet via dotnet-install.sh
#   pwsh 7     as a .NET global tool (dotnet tool install -g PowerShell)
#   Terraform  the ci-dotnet image's pin (containers/platform/ci-dotnet/Dockerfile), within terraform/**/versions.tf
#              (required_version >= 1.11.0), checked against its sha256
# Bash, not PowerShell 7: it installs pwsh, so it runs before pwsh exists (TB23 permanent exception).
# Idempotent; does nothing outside Claude Code on the web.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

repo="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
dotnet_root="$HOME/.dotnet"
tools_dir="$dotnet_root/tools"
bin_dir="$HOME/.local/bin"
terraform_version="1.16.4"
terraform_sha256="dc94af0eef1147718ad7c8daea792ed199e3e0492eec180d0adafa2a65a879df"

export DOTNET_ROOT="$dotnet_root" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export PATH="$dotnet_root:$tools_dir:$bin_dir:$PATH"

# .NET SDK: any installed SDK of the same major.minor and a feature band at or above global.json's satisfies latestFeature.
sdk_version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([0-9.]*\)".*/\1/p' "$repo/tests/global.json" | head -n 1)"
IFS=. read -r sdk_major sdk_minor sdk_patch <<<"$sdk_version"
sdk_band=$((sdk_patch / 100))
sdk_ok=false
if [ -x "$dotnet_root/dotnet" ]; then
  while IFS=. read -r major minor patch; do
    patch="${patch%% *}"
    patch="${patch%%-*}"
    if [ "$major" = "$sdk_major" ] && [ "$minor" = "$sdk_minor" ] && [ $((patch / 100)) -ge "$sdk_band" ]; then
      sdk_ok=true
    fi
  done < <("$dotnet_root/dotnet" --list-sdks 2>/dev/null || true)
fi
if [ "$sdk_ok" != "true" ]; then
  echo "session-start: installing .NET SDK $sdk_version into $dotnet_root" >&2
  installer="$(mktemp)"
  curl -fsSL -o "$installer" https://dot.net/v1/dotnet-install.sh
  bash "$installer" --version "$sdk_version" --install-dir "$dotnet_root" --no-path >&2
  rm -f "$installer"
fi

# PowerShell 7 as a global tool.
if ! pwsh -NoProfile -Command 'exit 0' >/dev/null 2>&1; then
  echo "session-start: installing PowerShell 7 (dotnet global tool)" >&2
  dotnet tool uninstall -g PowerShell >/dev/null 2>&1 || true
  dotnet tool install -g PowerShell >&2
fi

# Terraform, verified against its published sha256.
if [ "$(terraform version 2>/dev/null | head -n 1)" != "Terraform v$terraform_version" ]; then
  echo "session-start: installing Terraform $terraform_version into $bin_dir" >&2
  work="$(mktemp -d)"
  curl -fsSL -o "$work/terraform.zip" "https://releases.hashicorp.com/terraform/${terraform_version}/terraform_${terraform_version}_linux_amd64.zip"
  printf '%s  %s\n' "$terraform_sha256" "$work/terraform.zip" | sha256sum -c - >&2
  mkdir -p "$bin_dir"
  if command -v unzip >/dev/null 2>&1; then
    unzip -q -o "$work/terraform.zip" terraform -d "$work"
  else
    python3 -c 'import sys, zipfile; zipfile.ZipFile(sys.argv[1]).extract("terraform", sys.argv[2])' "$work/terraform.zip" "$work"
  fi
  install -m 0755 "$work/terraform" "$bin_dir/terraform"
  rm -rf "$work"
fi

# Persist the environment for the session.
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  if ! grep -qs 'session-start: toolchain' "$CLAUDE_ENV_FILE"; then
    {
      echo '# session-start: toolchain'
      echo "export DOTNET_ROOT=\"$dotnet_root\""
      echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1'
      echo "export PATH=\"$dotnet_root:$tools_dir:$bin_dir:\$PATH\""
    } >> "$CLAUDE_ENV_FILE"
  fi
fi
