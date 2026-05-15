# Secrets and credential handling

This exporter handles provider credentials, tenant scrape tokens, and optional OTLP backend headers. Treat every one of them as production secrets. The exporter is designed to make LLM spend a near-real-time operational cost signal, not a place to centralize broader cloud administration power.

## Non-logging guarantee

Provider credentials are never intentionally logged by the exporter.

The code does not log outbound `Authorization`, `x-api-key`, `client_secret`, AWS signing, session-token, service-account private-key, or OTLP header values. Provider failure messages may include a short upstream response-body snippet for debugging, but that snippet is passed through `SecretRedactor` before it reaches logs, traces, or health state. The redactor removes configured secret values and common credential fields such as `access_token`, `client_secret`, `private_key`, `authorization`, `x-api-key`, `AWS_SECRET_ACCESS_KEY`, and `X-Amz-Security-Token`.

Do not enable raw HTTP wire logging, reverse-proxy request-body logging, or sidecar debug capture around the exporter unless your platform redacts headers and form bodies first.

## Further reading

- [docs/least-privilege.md](least-privilege.md) — exact IAM policy documents, service-account creation commands, and scope verification for every provider
- [docs/secret-delivery.md](secret-delivery.md) — Kubernetes Secret YAML, External Secrets Operator examples for AWS / GCP / Azure / Vault, Sealed Secrets, and rotation-triggered pod restart patterns
- [docs/tenant-isolation.md](tenant-isolation.md) — what is isolated per tenant registration, what is shared, blast-radius table, and per-tenant deployment models
- [deploy/network-policy/](../deploy/network-policy/) — NetworkPolicy YAML restricting ingress to Prometheus only, and Istio AuthorizationPolicy for mTLS-enforced scrape access

## Least-privilege provider summary

| Provider | Credential | Minimum access | Avoid |
|---|---|---|---|
| OpenAI | Organization admin API key dedicated to this exporter | Organization usage and costs reporting APIs | Reusing app inference keys, sharing one admin key across environments, broad human-owned keys |
| Anthropic Claude | Organization admin API key dedicated to this exporter | Admin usage report and cost report APIs | Regular inference API keys, owner personal keys, unfiltered all-workspace use when a workspace subset is enough |
| Azure OpenAI | Microsoft Entra service principal client secret | `Monitoring Reader` on each Azure OpenAI account plus `Cost Management Reader` at the narrowest subscription/resource-group scope that answers cost queries | `Owner`, `Contributor`, `Cognitive Services User`, or subscription-wide monitoring roles when account scope is enough |
| Google Gemini / Vertex AI | GCP service-account JSON keyfile or short-lived OAuth token | `roles/monitoring.viewer`; add BigQuery dataset read and query-job permissions only when cost queries are enabled | `roles/owner`, `roles/editor`, `roles/monitoring.admin`, project-wide BigQuery admin |
| AWS Bedrock | AWS access key + secret, preferably short-lived STS credentials synced from an assumed role | `cloudwatch:GetMetricData`; add `ce:GetCostAndUsage` only when cost queries are enabled | `AdministratorAccess`, `ReadOnlyAccess`, `bedrock:InvokeModel`, or static IAM-user keys in production |

Use provider filters (`*_PROJECT_IDS`, `*_WORKSPACE_IDS`, `*_MODELS`, `BEDROCK_MODEL_IDS`) to reduce emitted data volume and blast radius. Treat those filters as observability boundaries, not authorization boundaries.

## Kubernetes Secrets

Kubernetes `Secret` objects are only base64-encoded by default. In production:

- Enable encryption at rest for Kubernetes secrets in etcd.
- Restrict `get`, `list`, and `watch` on `secrets` with namespace-scoped RBAC.
- Put exporter credentials in the same namespace as the exporter and avoid sharing the namespace with unrelated workloads.
- Prefer `envFrom` or mounted secret volumes over inline values in Helm files.
- Do not paste decoded secret values into issues, CI logs, runbooks, Slack, or screenshots.
- Use checksum annotations or your secret operator's restart integration so pods restart after rotation.
- Consider immutable Kubernetes Secrets for static release snapshots, but remember rotation then requires creating a new Secret name and rolling the Deployment.

## External secret managers

Prefer an external manager as the source of truth, then sync into Kubernetes:

- AWS Secrets Manager or SSM Parameter Store through External Secrets Operator or Secrets Store CSI Driver.
- Azure Key Vault through External Secrets Operator, Key Vault CSI Driver, or workload identity based sync.
- Google Secret Manager through External Secrets Operator, Config Connector, or a controlled CI/CD sync.
- HashiCorp Vault through Vault Agent Injector, Vault CSI Provider, or External Secrets Operator.
- SOPS, sealed-secrets, or another GitOps encryption workflow if your platform stores desired state in git.

For cloud-provider credentials, prefer short-lived or automatically rotated credentials where the exporter implementation supports them. When the exporter only reads credentials at startup, rotate by updating the backing secret and restarting the pod.

## Rotation pattern

Use the same four-step pattern for every provider:

1. Create the new credential while the old credential remains valid.
2. Update the external secret or Kubernetes Secret.
3. Restart the exporter pod because provider credentials are read at startup.
4. Confirm `llm_exporter_poll_success_total{provider="..."}` increments, then revoke the old credential.

For urgent compromise response, revoke first and accept a short exporter outage. The lookback window catches up after the replacement credential is live.

## Log and trace redaction

The exporter logs provider name, tenant id, HTTP status, retry attempt, bucket counts, and poll duration. It does not log provider request bodies or credential headers. Trace tags follow the same rule: provider, tenant, and time-window metadata are allowed; credential values are not.

If you forward logs or traces to another system, configure that system to redact these fields too:

- `authorization`
- `x-api-key`
- `client_secret`
- `access_token`
- `private_key`
- `AWS_ACCESS_KEY_ID`
- `AWS_SECRET_ACCESS_KEY`
- `AWS_SESSION_TOKEN`
- `X-Amz-Security-Token`
- `OTEL_EXPORTER_OTLP_HEADERS`

The `/metrics`, `/focus.csv`, and `/focus.json` endpoints do not expose provider credentials, but they do expose operational cost and usage data. Keep them internal or protect them with mTLS, service-mesh authorization, a private scrape network, or an authenticated reverse proxy.
