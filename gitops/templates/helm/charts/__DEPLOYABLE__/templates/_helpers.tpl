{{/* Common labels of the chart's objects. */}}
{{- define "starter.labels" -}}
app.kubernetes.io/part-of: <app>
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
{{- end }}

{{/* An image reference from a values block with repository and tag; the tag is pinned per environment. */}}
{{- define "starter.image" -}}
{{- $tag := required (printf "%s.tag is pinned in envs/<env>/<deployable>/values.yaml" .name) .image.tag -}}
{{- printf "%s:%s" .image.repository $tag -}}
{{- end }}

{{/* DB_* variables from one of the database Secrets (db-app or db-migrator). */}}
{{- define "starter.dbEnv" -}}
{{- range $pair := list (list "DB_HOST" "host") (list "DB_PORT" "port") (list "DB_NAME" "database") (list "DB_USER" "username") (list "DB_PASSWORD" "password") }}
- name: {{ index $pair 0 }}
  valueFrom:
    secretKeyRef:
      name: {{ $.secret }}
      key: {{ index $pair 1 }}
{{- end }}
{{- end }}
