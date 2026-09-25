#!/usr/bin/env bash
# platform-pin-writer: the fallback pin writer (ADR-IR34 decision 20; CAP-OCT-012 runs against whichever writer is
# active). An app switches to it by pull request only if the Preview step "Update Argo CD Application Image Tags"
# breaks after an Octopus Cloud upgrade: that pull request replaces the image-tag step with a step of this template
# (or an inline copy) and gives the step the sensitive variable PinWriter.GitToken.
#
# octopus/terraform creates step template platform-pin-writer from this file. An inline copy sits between
# "# >>> octopus/step-templates/pin-writer.sh" and "# <<< octopus/step-templates/pin-writer.sh" (offline drift test).
# Bash only; no dollar-brace sequences (OCL heredoc template syntax).
#
# What it does, like the Preview step: commits images[].newTag (tags only; a digest next to the tag is removed, V3) in
# gitops/apps/<app>/envs/<env>/<deployable>/kustomization.yaml on the default branch, as the pin bot, then waits
# until Argo CD reports Application <app>-<deployable>-<env> Synced at that commit and Healthy. Kustomize
# deployables only; Helm and raw deployables keep the Preview step.
# Where it runs: k8s-<env> (variable Platform.WorkerPool). The wait reads applications.argoproj.io in namespace
# argocd as service account octopus-worker-<env>-scripts (Role octopus-worker-application-reader,
# argocd/clusters/<tier>/octopus-workers-rbac.yaml).
# Commits are authored by octopus-argocd-pin-bot, the identity of the bot-path audit (PLATFORM_BOT_AUTHORS).
#
# Inputs, shell variables set by the step header (inline copy) or from the template parameters:
#   PINWRITER_APP              app slug
#   PINWRITER_DEPLOYABLE       deployable name, for example app
#   PINWRITER_ENVIRONMENT      tdd, uat or prod
#   PINWRITER_IMAGES           image=tag pairs, separated by commas or new lines; images are names under apps/<app>/
#   PINWRITER_REPO_URL         environment repository; empty means the default below
#   PINWRITER_BRANCH           branch Argo CD reads; empty means main
#   PINWRITER_TIMEOUT_SECONDS  longest wait for Argo CD; empty means 900
# Sensitive Octopus variable PinWriter.GitToken: a token with contents write on the environment repository.
# Output variable: PinWriter.Commit.
# shellcheck disable=SC2154  # the inputs come from the step header
set -eu -o pipefail

app="$PINWRITER_APP"
deployable="$PINWRITER_DEPLOYABLE"
environment="$PINWRITER_ENVIRONMENT"
images="$PINWRITER_IMAGES"
repo_url="$PINWRITER_REPO_URL"
branch="$PINWRITER_BRANCH"
timeout_seconds="$PINWRITER_TIMEOUT_SECONDS"
[ -n "$repo_url" ] || repo_url="https://github.com/clearmeasure-aisf-sample-apps/basic-environment-octopus-codefresh.git"
[ -n "$branch" ] || branch="main"
[ -n "$timeout_seconds" ] || timeout_seconds=900
printf '%s' "$app" | grep -Eq '^[a-z][a-z0-9]{2,11}$' || fail_step "PINWRITER_APP '$app' is not an app slug."
printf '%s' "$deployable" | grep -Eq '^[a-z][a-z0-9]{1,11}$' || fail_step "PINWRITER_DEPLOYABLE '$deployable' is not a deployable name."
case "$environment" in
    tdd|uat|prod) ;;
    *) fail_step "PINWRITER_ENVIRONMENT must be tdd, uat or prod, not '$environment'." ;;
esac
case "$timeout_seconds" in
    ''|*[!0-9]*) fail_step "PINWRITER_TIMEOUT_SECONDS must be a whole number of seconds, not '$timeout_seconds'." ;;
esac
token="$(get_octopusvariable "PinWriter.GitToken")"
[ -n "$token" ] || fail_step "The sensitive variable PinWriter.GitToken is empty; the pull request that switches on the fallback writer must provide it."
command -v git >/dev/null 2>&1 || fail_step "git is missing from the step container."
command -v kubectl >/dev/null 2>&1 || fail_step "kubectl is missing from the step container."

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
chmod 700 "$work"
askpass="$work/askpass.sh"
{
    echo '#!/bin/sh'
    # shellcheck disable=SC2016,SC2028  # written literally; the askpass helper expands it
    echo 'case "$1" in Username*) echo x-access-token ;; *) printf "%s\n" "$PINWRITER_TOKEN" ;; esac'
} > "$askpass"
chmod 700 "$askpass"
# The token reaches git through the environment of the askpass helper, never through arguments or the remote URL.
export PINWRITER_TOKEN="$token" GIT_ASKPASS="$askpass" GIT_TERMINAL_PROMPT=0
git clone --quiet --depth 50 --branch "$branch" "$repo_url" "$work/repo"
git -C "$work/repo" config user.name "octopus-argocd-pin-bot"
git -C "$work/repo" config user.email "octopus-argocd-pin-bot@users.noreply.github.com"

file="gitops/apps/$app/envs/$environment/$deployable/kustomization.yaml"
[ -f "$work/repo/$file" ] || fail_step "$file does not exist: the fallback writer handles Kustomize deployables only."

# set_tag IMAGE TAG rewrites the newTag of the images[] entry named <registry>/apps/<app>/IMAGE and drops its digest.
set_tag() {
    awk -v suffix="apps/$app/$1" -v tag="$2" '
        function matches(n) { return n == suffix || substr(n, length(n) - length(suffix)) == "/" suffix }
        BEGIN { in_item = 0; matched = 0; updated = 0 }
        {
            line = $0
            if (line ~ /^[[:space:]]*-[[:space:]]+name:/) {
                value = line
                sub(/^[[:space:]]*-[[:space:]]+name:[[:space:]]*/, "", value)
                gsub(/["[:space:]]/, "", value)
                in_item = matches(value)
                if (in_item) { matched = 1 }
                print line
                next
            }
            if (in_item && line ~ /^[[:space:]]*-[[:space:]]/) { in_item = 0 }
            if (in_item && line ~ /^[^[:space:]]/) { in_item = 0 }
            if (in_item && line ~ /^[[:space:]]*newTag:/) { sub(/newTag:.*/, "newTag: \"" tag "\"", line); updated = 1 }
            if (in_item && line ~ /^[[:space:]]*digest:/) { next }
            print line
        }
        END { if (!matched) { exit 3 } if (!updated) { exit 4 } }
    ' "$work/repo/$file" > "$work/kustomization.yaml" || return $?
    cp "$work/kustomization.yaml" "$work/repo/$file"
}

summary=""
pairs="$(printf '%s' "$images" | tr ',' '\n')"
while IFS= read -r pair; do
    pair="$(printf '%s' "$pair" | tr -d '[:space:]')"
    [ -n "$pair" ] || continue
    image="$(printf '%s' "$pair" | cut -d= -f1)"
    tag="$(printf '%s' "$pair" | cut -d= -f2-)"
    printf '%s' "$image" | grep -Eq '^[a-z][a-z0-9-]{0,38}[a-z0-9]$' || fail_step "Image name '$image' is invalid (PINWRITER_IMAGES)."
    printf '%s' "$tag" | grep -Eq '^[A-Za-z0-9_][A-Za-z0-9._-]{0,127}$' || fail_step "Tag '$tag' of $image is invalid (PINWRITER_IMAGES)."
    status=0
    set_tag "$image" "$tag" || status=$?
    case "$status" in
        0) summary="$summary $image=$tag" ;;
        3) fail_step "$file has no images[] entry for apps/$app/$image." ;;
        4) fail_step "The images[] entry for apps/$app/$image in $file has no newTag line." ;;
        *) fail_step "Updating $file failed (awk exit $status)." ;;
    esac
done <<< "$pairs"
[ -n "$summary" ] || fail_step "PINWRITER_IMAGES names no image."

if git -C "$work/repo" diff --quiet -- "$file"; then
    echo "$file already pins$summary; nothing to commit."
else
    git -C "$work/repo" add -- "$file"
    git -C "$work/repo" commit --quiet -m "pin($app/$deployable/$environment):$summary" -m "Octopus release $(get_octopusvariable "Octopus.Release.Number"), platform-pin-writer (ADR-IR34 decision 20)."
    attempt=1
    until git -C "$work/repo" push --quiet origin "HEAD:$branch"; do
        [ "$attempt" -lt 3 ] || fail_step "Pushing the pin commit to $branch failed three times."
        attempt=$((attempt + 1))
        sleep 5
        git -C "$work/repo" pull --quiet --rebase origin "$branch"
    done
fi
commit="$(git -C "$work/repo" rev-parse HEAD)"
set_octopusvariable "PinWriter.Commit" "$commit"
echo "Pinned$summary in $file at $commit."

application="$app-$deployable-$environment"
deadline=$(( $(date +%s) + timeout_seconds ))
while :; do
    state="$(kubectl --namespace argocd get application "$application" --output 'jsonpath={.status.sync.revision} {.status.sync.status} {.status.health.status}' 2>/dev/null)" || state=""
    read -r revision sync health <<< "$state" || true
    if [ "$revision" = "$commit" ] && [ "$sync" = "Synced" ] && [ "$health" = "Healthy" ]; then
        break
    fi
    [ "$(date +%s)" -lt "$deadline" ] || fail_step "Argo CD Application $application did not reach Synced and Healthy at $commit within $timeout_seconds seconds (last: '$state')."
    sleep 15
done
write_highlight "Argo CD Application $application is Synced at $commit and Healthy."
