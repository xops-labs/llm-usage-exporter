{{/*
Expand the name of the chart.
*/}}
{{- define "llm-usage-exporter.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Create a default fully qualified app name.
If release name contains chart name it is used as the full name.
*/}}
{{- define "llm-usage-exporter.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- $name := default .Chart.Name .Values.nameOverride -}}
{{- if contains $name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}
{{- end -}}

{{/*
Chart label.
*/}}
{{- define "llm-usage-exporter.chart" -}}
{{- printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Common labels.
*/}}
{{- define "llm-usage-exporter.labels" -}}
helm.sh/chart: {{ include "llm-usage-exporter.chart" . }}
{{ include "llm-usage-exporter.selectorLabels" . }}
{{- if .Chart.AppVersion }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
{{- end }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: llm-usage-exporter
{{- end -}}

{{/*
Selector labels.
*/}}
{{- define "llm-usage-exporter.selectorLabels" -}}
app.kubernetes.io/name: {{ include "llm-usage-exporter.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/*
Service account name.
*/}}
{{- define "llm-usage-exporter.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{- default (include "llm-usage-exporter.fullname" .) .Values.serviceAccount.name -}}
{{- else -}}
{{- default "default" .Values.serviceAccount.name -}}
{{- end -}}
{{- end -}}

{{/*
Name of the ConfigMap holding non-secret env vars.
*/}}
{{- define "llm-usage-exporter.configMapName" -}}
{{- printf "%s-config" (include "llm-usage-exporter.fullname" .) -}}
{{- end -}}

{{/*
Name of the Secret holding provider credentials.
*/}}
{{- define "llm-usage-exporter.secretName" -}}
{{- printf "%s-secrets" (include "llm-usage-exporter.fullname" .) -}}
{{- end -}}

{{/*
Name of the Secret holding only the Gemini service-account keyfile.
*/}}
{{- define "llm-usage-exporter.geminiKeyfileSecretName" -}}
{{- printf "%s-gemini-keyfile" (include "llm-usage-exporter.fullname" .) -}}
{{- end -}}
