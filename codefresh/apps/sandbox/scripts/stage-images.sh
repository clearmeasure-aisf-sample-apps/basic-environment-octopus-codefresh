#!/usr/bin/env bash
# Stages the lean Docker build contexts of the sandbox fixture app (contract §7.0 "Conformance":
# images apps/sandbox/web and apps/sandbox/migrator):
#   <out>/web/       Dockerfile (containers/apps/sandbox/web/) + publish/ (src/Sandbox.Web)
#   <out>/migrator/  Dockerfile and migrate.sh (containers/apps/sandbox/migrator/) + publish/
#                    (src/Sandbox.Migrator) + scripts/ (db/scripts, and db/toggles/*.sql when the
#                    commit carries the marker toggles/failing-migration; CAP-GIT-010)
# Run it from the sandbox checkout after `dotnet build -c Release`.
#
# Usage: stage-images.sh [--out <dir>] [--repo <dir>] [--containers <dir>]
set -euo pipefail

die() {
  printf 'stage-images.sh: %s\n' "$1" >&2
  exit 1
}

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${CF_VOLUME_PATH:-.}/image-contexts"
repo_root="$(pwd)"
containers_dir="$script_dir/../../../../containers/apps/sandbox"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --out)
      [ "$#" -ge 2 ] || die "--out needs a directory"
      out="$2"
      shift 2
      ;;
    --repo)
      [ "$#" -ge 2 ] || die "--repo needs a directory"
      repo_root="$2"
      shift 2
      ;;
    --containers)
      [ "$#" -ge 2 ] || die "--containers needs a directory"
      containers_dir="$2"
      shift 2
      ;;
    *)
      die "unknown argument: $1"
      ;;
  esac
done

repo_root="$(cd "$repo_root" && pwd)"
[ -f "$repo_root/Sandbox.sln" ] || die "$repo_root holds no Sandbox.sln"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

fresh() {
  local dir="${out:?}/${1:?}"
  rm -rf -- "$dir"
  mkdir -p "$dir"
  printf '%s' "$dir"
}

web="$(fresh web)"
dotnet publish "$repo_root/src/Sandbox.Web/Sandbox.Web.csproj" --configuration Release --no-restore --no-build --nologo -o "$web/publish"
[ -f "$web/publish/Sandbox.Web.dll" ] || die "web: publish/ is missing Sandbox.Web.dll"
cp "$containers_dir/web/Dockerfile" "$web/Dockerfile"

migrator="$(fresh migrator)"
dotnet publish "$repo_root/src/Sandbox.Migrator/Sandbox.Migrator.csproj" --configuration Release --no-restore --no-build --nologo -o "$migrator/publish"
[ -f "$migrator/publish/Sandbox.Migrator.dll" ] || die "migrator: publish/ is missing Sandbox.Migrator.dll"
mkdir -p "$migrator/scripts"
cp "$repo_root"/db/scripts/*.sql "$migrator/scripts/"
if [ -f "$repo_root/toggles/failing-migration" ]; then
  printf 'stage-images.sh: toggles/failing-migration is on; the migrator image carries a failing script\n' >&2
  cp "$repo_root"/db/toggles/*.sql "$migrator/scripts/"
fi
cp "$containers_dir/migrator/Dockerfile" "$migrator/Dockerfile"
install -m 0755 "$containers_dir/migrator/migrate.sh" "$migrator/migrate.sh"

printf 'stage-images.sh: contexts ready under %s: web, migrator (%s script(s))\n' "$out" "$(find "$migrator/scripts" -name '*.sql' | wc -l)" >&2
