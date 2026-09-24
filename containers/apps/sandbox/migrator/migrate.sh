#!/usr/bin/env bash
# Entrypoint of the platform's migrator images (containers/apps/<app>/db-migrator,
# containers/apps/sandbox/migrator): runs $MIGRATION_COMMAND with the container's arguments and
# retries until it succeeds or MIGRATION_DB_READY_TIMEOUT_SECONDS pass (contract §7.0
# "Migrations": the PreSync Job retries its login until the database is ready). A migration
# that keeps failing, for example a broken script, fails the Job once the time is up, which
# fails the Argo CD sync and keeps the old version serving (CAP-GIT-010).
#
# Environment:
#   MIGRATION_COMMAND                   the migration console (set by the image)
#   MIGRATION_DB_READY_TIMEOUT_SECONDS  total retry budget in seconds (default 300; 0 = one try)
#   MIGRATION_RETRY_INTERVAL_SECONDS    pause between attempts (default 10)
# Arguments are passed through unchanged; they may contain a password, so none is ever printed.
set -uo pipefail

timeout="${MIGRATION_DB_READY_TIMEOUT_SECONDS:-300}"
interval="${MIGRATION_RETRY_INTERVAL_SECONDS:-10}"
case "$timeout" in '' | *[!0-9]*) timeout=300 ;; esac
case "$interval" in '' | *[!0-9]*) interval=10 ;; esac
[ "$interval" -gt 0 ] || interval=1

# MIGRATION_COMMAND is split into words on purpose ("dotnet /app/<console>.dll").
read -r -a command <<<"${MIGRATION_COMMAND:?MIGRATION_COMMAND is not set}"

start="$(date +%s)"
attempt=1
while :; do
  "${command[@]}" "$@"
  status=$?
  if [ "$status" -eq 0 ]; then
    printf 'migrate.sh: migration succeeded (attempt %d)\n' "$attempt"
    exit 0
  fi
  elapsed=$(($(date +%s) - start))
  if [ "$elapsed" -ge "$timeout" ]; then
    printf 'migrate.sh: migration failed with exit code %d after %d attempt(s) in %ds\n' "$status" "$attempt" "$elapsed" >&2
    exit "$status"
  fi
  printf 'migrate.sh: attempt %d failed with exit code %d; retrying in %ds (%ds of %ds used)\n' "$attempt" "$status" "$interval" "$elapsed" "$timeout" >&2
  sleep "$interval"
  attempt=$((attempt + 1))
done
