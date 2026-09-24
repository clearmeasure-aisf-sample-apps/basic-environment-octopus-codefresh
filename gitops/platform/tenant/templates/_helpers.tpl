{{/*
gitops/platform/tenant/templates/_helpers.tpl

Shared definitions of the tenant chart. Every template starts with `include "tenant.validate"`,
which fails the render (and with it the sync of tenant-<app>) on a descriptor or platform value the
platform cannot fence: `platform-tenants` may create cluster-scoped objects, so the chart itself is
a guard (ADR-IR34; apps/schema.json holds the same rules for the onboarding tool).
*/}}

{{/* The app slug, validated. */}}
{{- define "tenant.app" -}}
{{- .Values.name -}}
{{- end -}}

{{/* Validation: renders nothing, fails on the first violation. */}}
{{- define "tenant.validate" -}}
{{- $app := .Values.name | toString -}}
{{- if not (regexMatch "^[a-z][a-z0-9]{2,11}$" $app) -}}
{{- fail (printf "tenant: name %q must match ^[a-z][a-z0-9]{2,11}$ (ADR-IR34 decision 10)" $app) -}}
{{- end -}}
{{- if has $app .Values.platform.reservedNames -}}
{{- fail (printf "tenant: name %q is reserved (ADR-IR34 decision 10)" $app) -}}
{{- end -}}
{{- if not (hasKey .Values.platform.tierEnvironments (.Values.platform.tier | toString)) -}}
{{- fail (printf "tenant: platform.tier %q must be nonprod or prod (values-<tier>.yaml)" (.Values.platform.tier | toString)) -}}
{{- end -}}
{{- if not (has (.Values.status | toString) (list "active" "frozen")) -}}
{{- fail (printf "tenant: status %q must be active or frozen" (.Values.status | toString)) -}}
{{- end -}}
{{- range .Values.environments -}}
{{- if not (has . (list "tdd" "uat" "prod")) -}}
{{- fail (printf "tenant: environment %q must be tdd, uat or prod" .) -}}
{{- end -}}
{{- end -}}
{{- if not .Values.deployables -}}
{{- fail "tenant: at least one deployable is required" -}}
{{- end -}}
{{- $projects := list -}}
{{- range ((.Values.octopus).projects | default list) -}}
{{- $projects = append $projects .name -}}
{{- end -}}
{{- $seen := dict -}}
{{- range .Values.deployables -}}
{{- $name := .name | toString -}}
{{- if or (not (regexMatch "^[a-z][a-z0-9]{1,11}$" $name)) (eq $name "db") -}}
{{- fail (printf "tenant: deployable %q must match ^[a-z][a-z0-9]{1,11}$ and not be db" $name) -}}
{{- end -}}
{{- if hasKey $seen $name -}}
{{- fail (printf "tenant: deployable %q is listed twice" $name) -}}
{{- end -}}
{{- $_ := set $seen $name true -}}
{{- if .part -}}
{{- if or (not (regexMatch "^[a-z][a-z0-9]{1,11}$" (.part | toString))) (has (.part | toString) (list "db" "pr")) -}}
{{- fail (printf "tenant: part %q of deployable %q must match ^[a-z][a-z0-9]{1,11}$ and not be db or pr" (.part | toString) $name) -}}
{{- end -}}
{{- end -}}
{{- if not (regexMatch (printf "^%s(-[a-z][a-z0-9]{1,11})?$" $app) (.octopusProject | toString)) -}}
{{- fail (printf "tenant: deployable %q: octopusProject %q must be %s or %s-<part>" $name (.octopusProject | toString) $app $app) -}}
{{- end -}}
{{- if and $projects (not (has .octopusProject $projects)) -}}
{{- fail (printf "tenant: deployable %q: octopusProject %q is not in octopus.projects" $name (.octopusProject | toString)) -}}
{{- end -}}
{{- if not (has (.packaging | toString) (list "kustomize" "helm" "raw")) -}}
{{- fail (printf "tenant: deployable %q: packaging %q must be kustomize, helm or raw" $name (.packaging | toString)) -}}
{{- end -}}
{{- if eq (.packaging | toString) "helm" -}}
{{- if not (and .helm .helm.chart .helm.imageReplacePaths) -}}
{{- fail (printf "tenant: deployable %q: packaging helm needs helm.chart and helm.imageReplacePaths" $name) -}}
{{- end -}}
{{- if or (hasPrefix "/" (.helm.chart | toString)) (contains ".." (.helm.chart | toString)) -}}
{{- fail (printf "tenant: deployable %q: helm.chart %q must be a path inside gitops/apps/%s/" $name (.helm.chart | toString) $app) -}}
{{- end -}}
{{- end -}}
{{- end -}}
{{- with .Values.database -}}
{{- if and .engine (ne (.engine | toString) "mssql-2022-express") -}}
{{- fail (printf "tenant: database.engine %q is not a platform component" (.engine | toString)) -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/* Environments of this cluster that the descriptor lists, as JSON. */}}
{{- define "tenant.envs" -}}
{{- $tierEnvs := get .Values.platform.tierEnvironments (.Values.platform.tier | toString) -}}
{{- $envs := list -}}
{{- range $tierEnvs -}}
{{- if has . $.Values.environments -}}
{{- $envs = append $envs . -}}
{{- end -}}
{{- end -}}
{{- toJson $envs -}}
{{- end -}}

{{/* Parts of the app (sorted, unique), as JSON. */}}
{{- define "tenant.parts" -}}
{{- $parts := list -}}
{{- range .Values.deployables -}}
{{- if .part -}}
{{- $parts = append $parts (.part | toString) -}}
{{- end -}}
{{- end -}}
{{- toJson ($parts | uniq | sortAlpha) -}}
{{- end -}}

{{/* Namespace of a deployable in an environment: (dict "root" $ "deployable" $d "env" $env). */}}
{{- define "tenant.namespace" -}}
{{- if .deployable.part -}}
{{- printf "%s-%s-%s" .root.Values.name .deployable.part .env -}}
{{- else -}}
{{- printf "%s-%s" .root.Values.name .env -}}
{{- end -}}
{{- end -}}

{{/* Namespaces of the app in an environment, main namespace first, as JSON: (dict "root" $ "env" $env). */}}
{{- define "tenant.namespaces" -}}
{{- $namespaces := list (printf "%s-%s" .root.Values.name .env) -}}
{{- range (include "tenant.parts" .root | fromJsonArray) -}}
{{- $namespaces = append $namespaces (printf "%s-%s-%s" $.root.Values.name . $.env) -}}
{{- end -}}
{{- toJson $namespaces -}}
{{- end -}}

{{/*
App vault kv-<app>-<e>-<hash4> (ADR-IR34 decision 8): hash4 is the first four hex digits of
sha1("4a4dfa6d-d434-4b9e-8a88-63bbf61cfb69/<app>/<env>") with the subscription ID trimmed and lowercase, the same
formula as terraform/apps/descriptor and Platform.Onboarding.
Argument: (dict "root" $ "env" $env).
*/}}
{{- define "tenant.vaultName" -}}
{{- $letter := get .root.Values.platform.environmentLetter .env -}}
{{- $subscription := .root.Values.platform.subscriptionId | toString | trim | lower -}}
{{- $hash := sha1sum (printf "%s/%s/%s" $subscription .root.Values.name .env) -}}
{{- printf "kv-%s-%s-%s" .root.Values.name $letter (substr 0 4 $hash) -}}
{{- end -}}

{{/* Labels of every rendered object. */}}
{{- define "tenant.labels" -}}
app.kubernetes.io/managed-by: platform-tenant
platform/app: {{ .Values.name }}
{{- end -}}

{{/* "true" when the descriptor parks the app (ADR-IR34 decision 28). */}}
{{- define "tenant.frozen" -}}
{{- if eq (.Values.status | toString) "frozen" -}}true{{- end -}}
{{- end -}}

{{/* "true" when platform.appsDomain is a valid DNS suffix (not a placeholder). */}}
{{- define "tenant.hasDomain" -}}
{{- if regexMatch "^[a-z0-9]([-a-z0-9]*[a-z0-9])?(\\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)+$" (.Values.platform.appsDomain | toString) -}}true{{- end -}}
{{- end -}}

{{/* Effective quota: the platform default, lowered by the descriptor (§7.0 Quotas), as JSON. */}}
{{- define "tenant.quota" -}}
{{- $default := .Values.platform.quota -}}
{{- $asked := .Values.quotas | default dict -}}
{{- $memory := minf ($asked.memoryGiB | default $default.memoryGiB | float64) ($default.memoryGiB | float64) -}}
{{- $cpu := minf ($asked.cpu | default $default.cpu | float64) ($default.cpu | float64) -}}
{{- $pvcs := $default.pvcs | int -}}
{{- if hasKey $asked "pvcs" -}}{{- $pvcs = min ($asked.pvcs | int) ($default.pvcs | int) -}}{{- end -}}
{{- $storage := min ($asked.storageGiB | default $default.storageGiB | int) ($default.storageGiB | int) -}}
{{- toJson (dict "memoryMi" (mulf $memory 1024 | int) "cpuMilli" (mulf $cpu 1000 | int) "pvcs" $pvcs "storageGi" $storage) -}}
{{- end -}}
