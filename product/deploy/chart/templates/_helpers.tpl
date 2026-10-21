{{/* Labels of a host's resources; the Scaling Lab finds the product's hosts by part-of (D31). */}}
{{- define "eventticketing.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: eventticketing
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
{{- end }}

{{- define "eventticketing.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: eventticketing
{{- end }}

{{- define "eventticketing.image" -}}
{{ .root.Values.image.repository }}/{{ .name }}:{{ .root.Values.image.tag }}
{{- end }}

{{/*
Settings every host gets: telemetry with the pod's name as the instance (the dashboard shows each
instance as a replica), the connection string for hosts that use the database, and `env`.
*/}}
{{- define "eventticketing.commonEnv" -}}
- name: POD_NAME
  valueFrom: { fieldRef: { fieldPath: metadata.name } }
{{- with .root.Values.telemetry.otlpEndpoint }}
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: {{ . | quote }}
{{- end }}
- name: OTEL_SERVICE_NAME
  value: {{ .name }}
- name: OTEL_RESOURCE_ATTRIBUTES
  value: service.instance.id=$(POD_NAME)
# Feature flags (FF1): the ConfigMap's file, re-read while running. Kubernetes swaps a ConfigMap's
# files by a symbolic link, which file watchers miss, so .NET polls (every 4 s).
- name: FeatureFlags__File
  value: /config/features/features.json
- name: DOTNET_USE_POLLING_FILE_WATCHER
  value: "true"
{{- if .database }}
- name: DATABASE_PASSWORD
  valueFrom: { secretKeyRef: { name: {{ .root.Values.database.cluster }}-app, key: password } }
- name: ConnectionStrings__eventticketing
  value: Host={{ .root.Values.database.cluster }}-rw;Database=eventticketing;Username=eventticketing;Password=$(DATABASE_PASSWORD);Maximum Pool Size={{ .root.Values.database.maxPoolSize }}
{{- end }}
{{- range $key, $value := .root.Values.env }}
- name: {{ $key }}
  value: {{ $value | quote }}
{{- end }}
{{- end }}

{{/* What each host needs besides that: the same settings dev/AppHost gives it under Aspire. */}}
{{- define "eventticketing.hostEnv" -}}
{{- $values := .root.Values -}}
{{- $secrets := $values.secrets.name -}}
{{- if or (eq .name "publicapi") (eq .name "backofficeapi") (eq .name "fansite") }}
- name: Authentication__Authority
  value: {{ $values.identity.authority | quote }}
- name: Authentication__RequireHttpsMetadata
  value: {{ $values.identity.requireHttpsMetadata | quote }}
{{- end }}
{{- if and $values.valkey (or (eq .name "publicapi") (eq .name "backofficeapi") (eq .name "workers")) }}
# Valkey (D51–D53): PublicApi caches browsing and reads availability snapshots, BackOfficeApi evicts
# browsing's answers on changes, Workers rebuilds the snapshots.
- name: ConnectionStrings__valkey
  value: {{ $values.valkey.connectionString | quote }}
{{- end }}
{{- if or (eq .name "publicapi") (eq .name "workers") }}
- name: Payments__LocalPay__BaseUrl
  value: {{ $values.urls.localPay | quote }}
{{- end }}
{{- if or (eq .name "webhookingress") (eq .name "workers") }}
- name: Payments__LocalPay__WebhookSecret
  valueFrom: { secretKeyRef: { name: {{ $secrets }}, key: localpay-webhook-secret } }
{{- end }}
{{- if eq .name "publicapi" }}
- name: Checkout__ReturnUrlTemplate
  value: {{ printf "%s/orders/{orderId}" $values.urls.fanSite | quote }}
{{- end }}
{{- if eq .name "workers" }}
- name: Ticketing__MasterKey
  valueFrom: { secretKeyRef: { name: {{ $secrets }}, key: ticketing-master-key } }
- name: Email__SmtpHost
  value: {{ $values.urls.smtpHost | quote }}
- name: Email__SmtpPort
  value: {{ $values.urls.smtpPort | quote }}
- name: Email__FanSiteUrl
  value: {{ $values.urls.fanSite | quote }}
- name: Organizers__Keycloak__BaseUrl
  value: {{ $values.urls.keycloak | quote }}
- name: Organizers__Keycloak__ClientSecret
  valueFrom: { secretKeyRef: { name: {{ $secrets }}, key: organizers-client-secret } }
{{- end }}
{{- if eq .name "fansite" }}
- name: PublicApi__BaseUrl
  value: http://publicapi:8080
- name: Authentication__ClientSecret
  valueFrom: { secretKeyRef: { name: {{ $secrets }}, key: fansite-client-secret } }
{{- end }}
{{- end }}

{{/* Non-root, nothing writable but two scratch directories, no extra privileges. */}}
{{- define "eventticketing.podSecurity" -}}
securityContext:
  runAsNonRoot: true
  seccompProfile: { type: RuntimeDefault }
{{- end }}

{{- define "eventticketing.containerSecurity" -}}
securityContext:
  allowPrivilegeEscalation: false
  readOnlyRootFilesystem: true
  capabilities: { drop: [ALL] }
volumeMounts:
  - { name: tmp, mountPath: /tmp }
  - { name: home, mountPath: /home/app }
  - { name: features, mountPath: /config/features, readOnly: true }
{{- end }}

{{- define "eventticketing.scratchVolumes" -}}
volumes:
  - { name: tmp, emptyDir: {} }
  - { name: home, emptyDir: {} }
  # Optional: the Migrator, a hook, runs before the ConfigMap exists on a first install.
  - { name: features, configMap: { name: eventticketing-features, optional: true } }
{{- end }}
