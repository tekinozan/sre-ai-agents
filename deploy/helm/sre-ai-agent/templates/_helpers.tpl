{{/*
Shared naming and the container spec every host repeats.

The three deployments differ only in composition (ARCHITECTURE.md 19.1), so the
template that renders them differs only in a name and a few environment
variables. Repeating the security context and probes per host is how they drift.
*/}}

{{- define "sre.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/part-of: sre-ai-agent
app.kubernetes.io/version: {{ .root.Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
{{- end -}}

{{- define "sre.image" -}}
{{ .root.Values.image.registry }}/{{ .component }}:{{ .root.Values.image.tag }}
{{- end -}}

{{/*
Hardened by default: non-root, no privilege escalation, read-only root and every
capability dropped. A host that needs more has to say so explicitly rather than
inheriting it.
*/}}
{{- define "sre.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: true
capabilities:
  drop: ["ALL"]
{{- end -}}

{{- define "sre.podSecurityContext" -}}
runAsNonRoot: true
seccompProfile:
  type: RuntimeDefault
{{- end -}}
