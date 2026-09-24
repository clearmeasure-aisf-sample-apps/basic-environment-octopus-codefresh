#!/usr/bin/env bash
# platform-db-backup: back up an app database before a release reaches it (ADR-IR34 decision 1; CAP-OCT-015).
#
# octopus/terraform creates step template platform-db-backup from this file. App processes may also inline it between
# "# >>> octopus/step-templates/db-backup.sh" and "# <<< octopus/step-templates/db-backup.sh" (offline drift test).
# Bash only; no dollar-brace sequences (OCL heredoc template syntax).
#
# Where it runs: the shared Kubernetes worker pool k8s-<env> (variable Platform.WorkerPool), whose script pods run in
# namespace octopus-worker-<env> as service account octopus-worker-<env>-scripts (terraform/tier). It creates a Job
# from the tenant chart's CronJob db-backup-<app>-<env> in namespace platform-backup (uat and prod only: tdd databases
# are disposable), labels it platform/trigger=pre-release and waits for it. The Job writes the backup to container
# <app>-<env> of <backup-storage-account-<tier>> as id-db-backup-<tier>.
# Cross-package interface (gitops): that service account needs, in namespace platform-backup, create on jobs, get on
# cronjobs, get and list on jobs and pods, and get on pods/log [VERIFY in the tenant chart or the bootstrap RBAC].
#
# Inputs, shell variables set by the step header (inline copy) or from the template parameters:
#   DBBACKUP_APP              app slug, ^[a-z][a-z0-9]{2,11}$
#   DBBACKUP_ENVIRONMENT      uat or prod
#   DBBACKUP_TIMEOUT_SECONDS  longest wait for the Job; empty means 1800
# Output variables: DbBackup.JobName, DbBackup.CompletedAt.
# shellcheck disable=SC2154  # the inputs come from the step header
set -eu -o pipefail

app="$DBBACKUP_APP"
environment="$DBBACKUP_ENVIRONMENT"
timeout_seconds="$DBBACKUP_TIMEOUT_SECONDS"
[ -n "$timeout_seconds" ] || timeout_seconds=1800
printf '%s' "$app" | grep -Eq '^[a-z][a-z0-9]{2,11}$' || fail_step "DBBACKUP_APP '$app' is not an app slug."
case "$environment" in
    uat|prod) ;;
    tdd) fail_step "tdd has no backup CronJob: tdd databases are disposable (ADR-IR34)." ;;
    *) fail_step "DBBACKUP_ENVIRONMENT must be uat or prod, not '$environment'." ;;
esac
case "$timeout_seconds" in
    ''|*[!0-9]*) fail_step "DBBACKUP_TIMEOUT_SECONDS must be a whole number of seconds, not '$timeout_seconds'." ;;
esac
command -v kubectl >/dev/null 2>&1 || fail_step "kubectl is missing from the step container."

namespace="platform-backup"
cronjob="db-backup-$app-$environment"
kubectl --namespace "$namespace" get cronjob "$cronjob" --output name >/dev/null ||
    fail_step "CronJob $namespace/$cronjob was not found. The tenant chart renders it for uat and prod when the descriptor declares a database."

job="$cronjob-pre-$(date -u +%y%m%d%H%M%S)"
release="$(printf '%s' "$(get_octopusvariable "Octopus.Release.Number")" | tr -c 'A-Za-z0-9._-' '-' | cut -c1-63)"
[ -n "$release" ] || release="none"
kubectl --namespace "$namespace" create job "$job" --from="cronjob/$cronjob" >/dev/null
kubectl --namespace "$namespace" label job "$job" "platform/trigger=pre-release" "platform/release=$release" --overwrite >/dev/null
echo "Backup Job $namespace/$job started from CronJob $cronjob for release $release."

deadline=$(( $(date +%s) + timeout_seconds ))
while :; do
    succeeded="$(kubectl --namespace "$namespace" get job "$job" --output 'jsonpath={.status.succeeded}')"
    failed="$(kubectl --namespace "$namespace" get job "$job" --output 'jsonpath={.status.conditions[?(@.type=="Failed")].status}')"
    if [ "$succeeded" = "1" ]; then
        break
    fi
    if [ "$failed" = "True" ]; then
        kubectl --namespace "$namespace" logs "job/$job" --tail=100 || true
        fail_step "Backup Job $namespace/$job failed; the release does not proceed to the pin."
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
        kubectl --namespace "$namespace" logs "job/$job" --tail=100 || true
        fail_step "Backup Job $namespace/$job did not complete within $timeout_seconds seconds."
    fi
    sleep 10
done

completed_at="$(kubectl --namespace "$namespace" get job "$job" --output 'jsonpath={.status.completionTime}')"
set_octopusvariable "DbBackup.JobName" "$job"
set_octopusvariable "DbBackup.CompletedAt" "$completed_at"
write_highlight "Database of $app in $environment backed up by Job $namespace/$job at $completed_at."
