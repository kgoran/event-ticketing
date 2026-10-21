{{- define "tooling.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: eventticketing-lab
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
{{- end }}

{{- define "tooling.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: eventticketing-lab
{{- end }}

{{/* A tool's Deployment, Service and route; `env` and `serviceAccount` vary. */}}
{{- define "tooling.app" -}}
{{- $root := .root }}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ .name }}
  labels:
    {{- include "tooling.labels" . | nindent 4 }}
spec:
  replicas: 1
  selector:
    matchLabels:
      {{- include "tooling.selector" . | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "tooling.labels" . | nindent 8 }}
    spec:
      {{- with .serviceAccount }}
      serviceAccountName: {{ . }}
      {{- end }}
      # The tools stand for the outside world, in zone-a, which no drill stops (D59): the load
      # generator must keep measuring while a zone fails.
      nodeSelector: { topology.kubernetes.io/zone: zone-a }
      securityContext:
        runAsNonRoot: true
        seccompProfile: { type: RuntimeDefault }
      containers:
        - name: {{ .name }}
          image: {{ $root.Values.image.repository }}/{{ .name }}:{{ $root.Values.image.tag }}
          imagePullPolicy: {{ $root.Values.image.pullPolicy }}
          ports:
            - { name: http, containerPort: 8080 }
          env:
            - name: POD_NAME
              valueFrom: { fieldRef: { fieldPath: metadata.name } }
            - name: OTEL_EXPORTER_OTLP_ENDPOINT
              value: {{ $root.Values.telemetry.otlpEndpoint | quote }}
            - name: OTEL_SERVICE_NAME
              value: {{ .name }}
            - name: OTEL_RESOURCE_ATTRIBUTES
              value: service.instance.id=$(POD_NAME)
            {{- toYaml .env | nindent 12 }}
          readinessProbe:
            httpGet: { path: /health, port: http }
            periodSeconds: 5
          resources:
            {{- toYaml $root.Values.resources | nindent 12 }}
          securityContext:
            allowPrivilegeEscalation: false
            readOnlyRootFilesystem: true
            capabilities: { drop: [ALL] }
          volumeMounts:
            - { name: tmp, mountPath: /tmp }
            - { name: home, mountPath: /home/app }
      volumes:
        - { name: tmp, emptyDir: {} }
        - { name: home, emptyDir: {} }
---
apiVersion: v1
kind: Service
metadata:
  name: {{ .name }}
  labels:
    {{- include "tooling.labels" . | nindent 4 }}
spec:
  selector:
    {{- include "tooling.selector" . | nindent 4 }}
  ports:
    - { name: http, port: 8080, targetPort: http }
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: {{ .name }}
  labels:
    {{- include "tooling.labels" . | nindent 4 }}
spec:
  parentRefs:
    - name: {{ $root.Values.gateway.name }}
      namespace: {{ $root.Values.gateway.namespace }}
      sectionName: {{ .name }}
  rules:
    - backendRefs:
        - { name: {{ .name }}, port: 8080 }
{{- end }}
