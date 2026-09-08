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

{{/*
Kafka__SaslUsername and Kafka__SaslPassword: named keys read individually via
secretKeyRef from the externally managed Secret (ARCHITECTURE.md 13), so a
rendered release shows exactly which keys it depends on instead of an opaque
bulk secretRef. `optional: true` lets a Plaintext (non-SASL) install render
without those keys existing in the Secret at all.
*/}}
{{- define "sre.kafkaSecretEnv" -}}
- name: Kafka__SaslUsername
  valueFrom:
    secretKeyRef:
      name: {{ .Values.kafka.secretName | default .Values.existingSecret }}
      key: {{ .Values.kafka.saslUsernameKey }}
      optional: true
- name: Kafka__SaslPassword
  valueFrom:
    secretKeyRef:
      name: {{ .Values.kafka.secretName | default .Values.existingSecret }}
      key: {{ .Values.kafka.saslPasswordKey }}
      optional: true
{{- end -}}

{{/*
A private Kafka CA, mounted read-only — never PEM content in values or the
ConfigMap, only the name of a Secret an operator manages out of band and the
key inside it. Empty caSecret renders neither the volume nor its mount.
*/}}
{{- define "sre.kafkaCaVolumeMount" -}}
{{- if .Values.kafka.caSecret }}
- name: kafka-ca
  mountPath: /etc/sre-ai-agent/kafka-ca
  readOnly: true
{{- end }}
{{- end -}}

{{- define "sre.kafkaCaVolume" -}}
{{- if .Values.kafka.caSecret }}
- name: kafka-ca
  secret:
    secretName: {{ .Values.kafka.caSecret }}
    defaultMode: 0440
{{- end }}
{{- end -}}

{{/*
The bootstrap credential reference resolves as env://SRE_LLM_CREDENTIAL
(ExternalReferenceCredentialResolver); this supplies it via the same
externally managed Secret. Rendered only when llm.bootstrap.credentialSecretKey
is set — a non-production install that leaves llm.bootstrap empty renders
neither this nor any Llm__Bootstrap__* ConfigMap key (see config.yaml).
*/}}
{{- define "sre.llmCredentialEnv" -}}
{{- with .Values.llm.bootstrap }}
{{- if .credentialSecretKey }}
- name: SRE_LLM_CREDENTIAL
  valueFrom:
    secretKeyRef:
      name: {{ $.Values.existingSecret }}
      key: {{ .credentialSecretKey }}
{{- end }}
{{- end }}
{{- end -}}
