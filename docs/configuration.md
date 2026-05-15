# Configuration reference

All configuration is environment-variable based (12-factor friendly). Every provider is **independently opt-in** — leave a provider's required variables unset and the exporter skips registration entirely for that provider (no validation, no polling, no metrics). You can run a single-provider deployment with only that provider's env vars set, or any combination.

**Setting up credentials in your cloud console:** the [`docs/credentials/`](credentials/) directory has a step-by-step runbook for each provider — IAM roles, console UI flow, CLI alternative, verification curl, rotation, and common errors:

- [OpenAI](credentials/openai.md) — admin API key
- [Azure OpenAI](credentials/azure-openai.md) — Azure AD service principal
- [Anthropic Claude](credentials/anthropic.md) — admin API key
- [Google Gemini](credentials/gemini.md) — GCP service account
- [AWS Bedrock](credentials/aws-bedrock.md) — IAM role (IRSA) or user

## Exporter

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `EXPORTER_POLL_INTERVAL_SECONDS` | No | `300` | Poll interval across all providers. |
| `EXPORTER_LOOKBACK_MINUTES` | No | `60` | Rolling lookback window per poll. |

## OpenAI

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `OPENAI_ADMIN_API_KEY` | Yes | none | OpenAI organization admin API key. |
| `OPENAI_ORG_ID` | No | none | Optional organization ID, sent as `OpenAI-Organization`. |
| `OPENAI_PROJECT_IDS` | No | empty | Comma-separated project IDs to filter. |
| `OPENAI_MODELS` | No | empty | Comma-separated model filter. |
| `OPENAI_API_KEY_IDS` | No | empty | Comma-separated API-key-ID filter. |
| `OPENAI_USER_IDS` | No | empty | Comma-separated user-ID filter. |
| `EXPORTER_GROUP_BY` | No | `model,project_id` | OpenAI usage grouping. |

## Azure OpenAI

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `AZURE_OPENAI_TENANT_ID` | Yes | none | Azure AD tenant. |
| `AZURE_OPENAI_CLIENT_ID` | Yes | none | Service-principal client ID. |
| `AZURE_OPENAI_CLIENT_SECRET` | Yes | none | Service-principal secret. |
| `AZURE_OPENAI_SUBSCRIPTION_ID` | Yes | none | Subscription scope for Cost Management. |
| `AZURE_OPENAI_RESOURCE_GROUP` | No | none | Narrow the cost query to a single resource group. |
| `AZURE_OPENAI_ACCOUNT_RESOURCE_IDS` | Yes | empty | Comma-separated ARM resource IDs of Azure OpenAI accounts to scrape. |
| `AZURE_OPENAI_MODELS` | No | empty | Comma-separated deployment-name filter. |

## Anthropic Claude

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `ANTHROPIC_ADMIN_API_KEY` | Yes | none | Anthropic organization admin API key. |
| `ANTHROPIC_VERSION` | No | `2023-06-01` | Value for the `anthropic-version` header. |
| `ANTHROPIC_WORKSPACE_IDS` | No | empty | Comma-separated workspace-ID filter. |
| `ANTHROPIC_MODELS` | No | empty | Comma-separated model filter. |
| `ANTHROPIC_API_KEY_IDS` | No | empty | Comma-separated API-key-ID filter. |

## Google Gemini

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `GEMINI_PROJECT_ID` | Yes | none | GCP project hosting Vertex AI usage. |
| `GEMINI_ACCESS_TOKEN` | One of | none | Pre-issued OAuth2 access token. |
| `GEMINI_SERVICE_ACCOUNT_KEY_FILE` | One of | none | Path to a service-account JSON keyfile (JWT-bearer exchange). |
| `GEMINI_ENABLE_COST_QUERIES` | No | `false` | Enable BigQuery billing-export cost lookups. |
| `GEMINI_BILLING_PROJECT_ID` | If cost on | `$GEMINI_PROJECT_ID` | Project used to run the BigQuery query. |
| `GEMINI_BILLING_DATASET_PROJECT` | If cost on | none | Project that owns the billing dataset. |
| `GEMINI_BILLING_DATASET_ID` | If cost on | none | BigQuery dataset ID. |
| `GEMINI_BILLING_TABLE` | If cost on | none | Billing-export table name (`gcp_billing_export_v1_<account>`). |
| `GEMINI_MODELS` | No | empty | Comma-separated model-ID filter. |

## AWS Bedrock

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `AWS_ACCESS_KEY_ID` | Yes | none | IAM access key. |
| `AWS_SECRET_ACCESS_KEY` | Yes | none | IAM secret. |
| `AWS_SESSION_TOKEN` | No | none | For STS / role-assumed temporary credentials. |
| `AWS_REGION` / `BEDROCK_REGION` | No | `us-east-1` | Region for CloudWatch metrics. |
| `BEDROCK_MODEL_IDS` | No | empty | Comma-separated model-ID filter. |
| `BEDROCK_ENABLE_COST_QUERIES` | No | `true` | Toggle Cost Explorer calls. |

## OpenTelemetry OTLP export

Setting `OTEL_EXPORTER_OTLP_ENDPOINT` activates a parallel OTLP export pipeline for metrics **and** traces. W3C `traceparent` propagation is enabled by default for provider HTTP calls and can be disabled with `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED=false` when trace IDs crossing vendor boundaries are not allowed.

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | One of | none | OTLP endpoint URL. Setting this enables the OTel export pipeline. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | No | `grpc` | `grpc` or `http/protobuf`. |
| `OTEL_EXPORTER_OTLP_HEADERS` | No | empty | OTLP headers, comma-separated `k=v` pairs (e.g. `api-key=...`). |
| `OTEL_SERVICE_NAME` | No | `llm-usage-exporter` | Service-name resource attribute. |
| `OTEL_SERVICE_VERSION` | No | current build | Service-version resource attribute. |
| `OTEL_METRIC_EXPORT_INTERVAL_SECONDS` | No | `60` | Periodic exporter cadence for `llm.usage.*` instruments. |
| `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE` | No | `Cumulative` | Metric temporality sent to the OTLP backend: `Cumulative` (default) keeps a running total since startup; `Delta` emits only the increment since the last export interval; `LowMemory` uses delta where possible. Most Prometheus-compatible backends (Grafana Mimir, VictoriaMetrics, AMP, Grafana Cloud) expect `Cumulative`. Delta-preferring backends (Datadog, New Relic) should either use this var or the OTel Collector `cumulativetodelta` processor — see [deploy/otel-collector/README.md](../deploy/otel-collector/README.md#metric-temporality). |
| `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED` | No | `true` | Controls outbound W3C Trace Context propagation to provider APIs. Set to `false` to strip `traceparent` / `tracestate` from provider HTTP requests while keeping exporter-local spans and metrics. |

## Durable checkpoints

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `CHECKPOINTS_PROVIDER` | No | `InMemory` | `InMemory` (default, stateless) or `File` (persist seen-bucket identities). |
| `CHECKPOINTS_FILE_PATH` | If `File` | `./data/checkpoints.jsonl` | JSON-lines file path used by the file-backed store. |
| `CHECKPOINTS_MAX_ENTRIES` | No | `100000` | Cap on retained identities before compaction. |
| `CHECKPOINTS_RETENTION_HOURS` | No | `168` | Identities older than this are dropped on compaction. |
| `CHECKPOINTS_FLUSH_INTERVAL_SECONDS` | No | `30` | How often the flush hosted service persists pending entries. |

## Alerts (budget burn + cost / token anomaly)

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `ALERTS_ENABLED` | No | `true` | Toggle the alert evaluator hosted service. |
| `ALERTS_EVALUATION_INTERVAL_SECONDS` | No | `60` | How often the evaluator drains the in-memory alert source. |
| `ALERTS_ROLLING_WINDOW_BUCKETS` | No | `60` | Buckets per `(tenant, provider, model)` for the anomaly z-score. |

Budgets themselves are a structured list and **cannot be set via env vars** — they need to come from `appsettings.json` or a mounted ConfigMap. Each entry: `Name`, `LimitUsd`, `Period` (`Monthly` / `Weekly` / `Daily`), optional `Providers` / `Models` / `Tenancies` / `Tenants` filters. `Tenancies` matches against the unified `tenancy_id` label (OpenAI / Gemini project, Anthropic workspace, Azure OpenAI resource, Bedrock region). See [docs/deployment.md](deployment.md#step-3--wire-budgets-via-a-configmap) for the canonical ConfigMap example.

## FOCUS export

| Variable | Required | Default | Description |
| --- | --- | --- | --- |
| `FOCUS_ENABLED` | No | `true` | Enable `/focus.csv` and `/focus.json` endpoints. |
| `FOCUS_MAX_RECORDS` | No | `50000` | In-memory record cap; oldest records are dropped on overflow. |

## Multi-tenant mode

Multi-tenant mode is configuration-driven. Populate the `Tenants` section in `appsettings.json` (or environment-variable equivalents) with one item per tenant, each carrying its own per-provider credential blocks. When `Tenants.Items` is empty, the exporter runs in single-tenant mode and every metric emits `tenant="default"`.

`/metrics?tenant=<id>` filters the exposition to a single tenant; when `Tenants.ApiKeys.<id>` is set, the request must carry `Authorization: Bearer <token>` matching that tenant's token.

See [docs/developer-guide.md → Step 10](developer-guide.md#step-10--multi-tenant-deployment) for a worked multi-tenant example.

## Example `.env`

```env
# --- Exporter ---
EXPORTER_POLL_INTERVAL_SECONDS=300
EXPORTER_LOOKBACK_MINUTES=60

# --- OpenAI ---
OPENAI_ADMIN_API_KEY=sk-admin-replace-me
OPENAI_ORG_ID=org_replace_me
OPENAI_PROJECT_IDS=proj_abc,proj_def

# --- Azure OpenAI (uncomment the block to enable) ---
# AZURE_OPENAI_TENANT_ID=00000000-0000-0000-0000-000000000000
# AZURE_OPENAI_CLIENT_ID=00000000-0000-0000-0000-000000000000
# AZURE_OPENAI_CLIENT_SECRET=replace-me
# AZURE_OPENAI_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000
# AZURE_OPENAI_ACCOUNT_RESOURCE_IDS=/subscriptions/.../accounts/contoso-aoai

# --- Anthropic Claude (uncomment the block to enable) ---
# ANTHROPIC_ADMIN_API_KEY=sk-ant-admin-replace-me

# --- Google Gemini (uncomment the block to enable) ---
# GEMINI_PROJECT_ID=my-gcp-proj
# GEMINI_SERVICE_ACCOUNT_KEY_FILE=/secrets/gemini-sa.json
# GEMINI_ENABLE_COST_QUERIES=false

# --- AWS Bedrock (uncomment the block to enable) ---
# AWS_ACCESS_KEY_ID=AKIAxxxxxxxxxxxxxxxx
# AWS_SECRET_ACCESS_KEY=replace-me
# AWS_REGION=us-east-1

# --- OpenTelemetry OTLP export (uncomment to enable) ---
# OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
# OTEL_EXPORTER_OTLP_PROTOCOL=grpc
# OTEL_SERVICE_NAME=llm-usage-exporter

# --- Durable checkpoints (uncomment to switch off in-memory) ---
# CHECKPOINTS_PROVIDER=File
# CHECKPOINTS_FILE_PATH=/data/checkpoints.jsonl

# --- Alerts (defaults on) ---
# ALERTS_ENABLED=true
# ALERTS_ROLLING_WINDOW_BUCKETS=60

# --- FOCUS v1.0 export (defaults on) ---
# FOCUS_ENABLED=true
# FOCUS_MAX_RECORDS=50000
```

The shipped [`.env.example`](../.env.example) in the repo root contains this same content with inline comments pointing at each credential runbook.

## See also

- [docs/metrics.md](metrics.md) — what each enabled provider emits
- [docs/credentials/](credentials/) — how to obtain each credential
- [docs/deployment.md](deployment.md) — production deployment with Helm-managed configuration
