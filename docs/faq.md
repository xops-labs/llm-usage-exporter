# Frequently asked questions

## What is `llm-usage-exporter`?

`llm-usage-exporter` is an **open-source Prometheus exporter for LLM usage and cost data**. It polls the OpenAI, Azure OpenAI, Anthropic, Google Gemini, and AWS Bedrock provider APIs on a configurable interval, normalizes their per-bucket records into Prometheus counters and gauges, and exposes them at `/metrics`. Drop it next to your existing Prometheus and Grafana, point it at one or more provider credentials, and you get a near-real-time signal for **token and request throughput** (available within minutes from provider metering APIs) and a **delayed-but-alertable signal for USD cost** (hours to days behind, depending on the provider's billing-history API — see [docs/metrics.md → Data freshness by provider](metrics.md#data-freshness-by-provider)).

## What is LLM cost observability and why does it matter?

LLM cost observability is the practice of treating **AI workload spend** as a first-class telemetry signal — alongside latency, error rate, and throughput — so engineering and FinOps teams can detect cost regressions, attribute spend to projects, alert on budget burn, and make data-driven scaling decisions. It matters because AI spend is the **fastest-growing cloud line item** and moves *hourly*, not monthly — billing dashboards alone arrive too late to prevent surprises.

## How is `llm-usage-exporter` different from a provider billing dashboard?

OpenAI / Azure / GCP / AWS billing dashboards are excellent for retrospective monthly review, but they are not usually **near-real-time, programmatic, alertable, queryable in PromQL, or cross-provider in one place**. `llm-usage-exporter` converts provider usage and cost history data into a unified Prometheus metric stream, which means you can: (a) alert on token spikes within minutes, and on cost movement hours before a billing dashboard would show it; (b) attribute spend per project / workspace / resource in your existing Grafana dashboards; (c) join AI cost with latency and error-rate SLOs; (d) trigger budget-burn workflows through your existing Alertmanager + PagerDuty + Slack pipeline; (e) compare cost-per-token across OpenAI, Azure OpenAI, Anthropic, Gemini, and Bedrock in the same panel. Note that cost signals are still delayed relative to the actual API calls — by 1–2 hours for OpenAI/Anthropic, and up to 24–72 hours for Azure, Bedrock, and Gemini.

## Is this invoice-accurate?

**No.** Treat the exporter as a near-real-time operational cost signal, not final billing reconciliation. It is designed to help engineering and FinOps teams notice cost movement early, route budget alerts, and investigate provider/model/tenant trends. Final reconciliation still belongs to provider invoices, billing exports, and finance-owned systems.

Provider rounding, credits, discounts, delayed cost finalization, currency treatment, tax, committed-use adjustments, marketplace/private-pricing terms, and invoice corrections can differ from exported counters. The exporter should be close enough to be operationally useful; it should not be used as the legal or contractual source of record.

## Does `llm-usage-exporter` send my data anywhere?

**No.** The exporter is fully self-hosted. It talks to each provider's official admin / monitoring / billing API to read your own usage data, and exposes that data on your own `/metrics` endpoint inside your own network. There is no SaaS backend, no telemetry, no phone-home, and no third-party SDK. The Docker image is published to GHCR and is pulled by you on your terms.

## Which LLM providers does it support today?

**Today: OpenAI, Azure OpenAI, Anthropic Claude, Google Gemini, and AWS Bedrock.** Each provider has its own typed HTTP client, retry/backoff policy, and Prometheus publisher; failures are isolated per-provider so a missing Azure tenant doesn't stop OpenAI scraping.

- **OpenAI** — Organization Usage and Costs APIs (token counts, requests, USD spend, grouping by model/project/line-item).
- **Azure OpenAI** — Azure Monitor metrics (`ProcessedPromptTokens`, `GeneratedTokens`, `TokenTransaction`) joined with Azure Cost Management query data; auth via Azure AD client-credentials.
- **Anthropic Claude** — Admin API `usage_report/messages` and `cost_report` endpoints with cursor pagination and prompt-caching split.
- **Google Gemini** — Vertex AI token counts from Cloud Monitoring `timeSeries.list`, optional BigQuery billing-export cost queries; auth via static access token or service-account JWT-bearer (no Google SDK dependency).
- **AWS Bedrock** — CloudWatch `GetMetricData` for invocation/token metrics, Cost Explorer for spend; requests signed inline with AWS SigV4 (no AWS SDK dependency).

## How does it avoid blowing up my Prometheus cardinality?

Two design choices. First, **`user_id` and API-key labels are intentionally excluded** — these are the biggest drivers of cardinality explosions in LLM observability tooling. Second, **labels are normalized** across every provider: trimmed, control characters replaced, and null or empty values mapped to `"unknown"` rather than allowed to drift. Power users who want higher-cardinality labels can opt in via configuration, knowing the Prometheus cost trade-off.

## How often does it poll each provider?

The default poll interval is **300 seconds** (5 minutes), configurable via `EXPORTER_POLL_INTERVAL_SECONDS`. The same interval applies to every configured provider — the polling worker iterates all registered providers each tick. A rolling lookback window (default `EXPORTER_LOOKBACK_MINUTES=60`) absorbs provider-side bucket-finalization delays without double-counting — repeated buckets are deduplicated in memory, independently per provider.

## How do I export the metrics into OpenTelemetry / OTLP?

Native OTLP export ships today. Set `OTEL_EXPORTER_OTLP_ENDPOINT` (and optionally `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`) and the exporter emits canonical `llm.usage.*` instruments (input/output/total tokens, requests, USD cost — tagged with `provider`, `model`, `tenant_id`, `tenant`) over OTLP gRPC or HTTP alongside the `/metrics` Prometheus surface. Works with every OTLP-compatible backend — Datadog, New Relic, Honeycomb, Grafana Cloud, AWS X-Ray, Azure Monitor, Google Cloud Operations, Splunk Observability. See [docs/developer-guide.md → Step 6](developer-guide.md#step-6--wire-grafana-self-hosted-or-grafana-cloud) for a Grafana Cloud walkthrough and [deploy/otel-collector/README.md](../deploy/otel-collector/README.md) for the OTel Collector pattern.

## Can I run `llm-usage-exporter` in production?

**Production-shaped, pre-1.0.** The exporter is a single ASP.NET Core process with a small surface area, stateless workers, structured logging, exporter-self-health metrics, multi-arch container images, CI on Ubuntu + Windows, CodeQL security scanning, and a tag-driven release pipeline with cosign keyless signing, SLSA L3 build provenance, and CycloneDX + SPDX SBOMs. 86 unit tests cover every provider's response mapping, retry behavior, multi-tenant DI registration, and the alert / FOCUS / OTLP pipelines. The five providers have been verified end-to-end against their real upstream APIs (authentication paths reach the upstream and produce the expected error shapes on bad credentials).

**What "production-shaped" means here, honestly:** the moving parts work and the metric / alert / config surface is stable enough to operate against. What it does *not* yet mean is "battle-tested across many real production deployments and long-running workloads" — this is still a v0.x project with no widely-reported production usage history. The cost of being early is yours to weigh against the upside of getting AI cost into Prometheus today.

**Recommended adoption path for any production use:**

1. Pin to a specific tagged version — never deploy `:latest`.
2. Run `cosign verify` as a deployment gate (the supply-chain story is one of the project's strongest points — use it).
3. Pilot with one team for two weeks with realistic traffic before broad rollout.
4. Validate the metric surface against your dashboards and budget definitions before letting Alertmanager page on it.
5. Follow [docs/deployment.md](deployment.md) — the full production checklist is there.

Single-replica deployments are best supported today; multi-replica horizontal scale requires sharding tenants across multiple deployments (the `FileCheckpointStore` is local to one pod). See [docs/deployment.md → Step 7](deployment.md#step-7--high-availability-and-the-multi-replica-caveat).

When the project hits its first stable release (`v1.0.0`), this answer changes. Until then: trust the code, verify the signatures, pilot before you rollout.

## What's the license?

**[Apache License 2.0](../LICENSE).** Permissive, OSI-approved, allows commercial use, modification, distribution, and private use. See [LICENSE](../LICENSE) for full terms and [NOTICE](../NOTICE) for attribution requirements.

## How do I report a security issue?

**Privately**, via GitHub Security Advisories or the contact in [SECURITY.md](../SECURITY.md). Please do not open a public issue for security problems.

## How do I contribute?

Read [CONTRIBUTING.md](../CONTRIBUTING.md) for the workflow. Good first issues are tagged `good first issue`; help-wanted issues are tagged `help wanted`. Pick a roadmap item or fix a bug; open a draft PR early so we can align on direction. All contributions are accepted under the Apache-2.0 license.

## Who maintains this project?

See [MAINTAINERS.md](../MAINTAINERS.md). The project is currently maintained by the [@xops-labs/maintainers](https://github.com/orgs/xops-labs/teams/maintainers) team. Governance is intentionally lightweight while the project is young; the role ladder, nomination process, and decision-making rules are documented in [GOVERNANCE.md](../GOVERNANCE.md).

## Why is `/metrics` empty after 5 minutes?

Wait one more poll cycle. Real metrics first appear when the first successful poll completes, which can take up to `EXPORTER_POLL_INTERVAL_SECONDS` (default 300s). If you've waited longer, check `<provider>_exporter_poll_success_total` — if it's zero, polling is failing. See [docs/troubleshooting.md → Polling problems](troubleshooting.md).

## Can I run it without Docker?

Yes: `dotnet run --project src/LlmUsageExporter.Api` after `dotnet restore`. You'll need the .NET 10 SDK (pinned by [`global.json`](../global.json)). The Docker path is still recommended because it pins the *runtime* image as well.

## Why does Anthropic show empty for `requests_total`?

Anthropic's organization admin API doesn't expose request counts — only token counts and cost. The exporter doesn't synthesize what the upstream doesn't return. Use `llm_usage_total_tokens_total{provider="anthropic"}` as a proxy for activity. Full availability matrix in [docs/metrics.md](metrics.md).

## What happens when a provider changes their API or returns unexpected fields?

**The exporter is resilient by design to backward-compatible upstream changes, and fails loudly for breaking ones.**

Provider usage and cost APIs occasionally add fields, rename models, or change pagination behavior. The exporter's JSON deserialization uses explicit field mapping with `[JsonPropertyName]` attributes and ignores unknown fields — new response fields from the provider do not break deserialization. New model names flow through automatically as new `model` label values (and count toward cardinality if unfiltered).

**What can break:**
- An endpoint URL rename or removal causes poll failures logged at ERROR level, with `llm_exporter_poll_failure_total` incrementing and `LlmUsageExporterPollFailures` alerting after 3+ failures in 15 minutes.
- A field the exporter depends on is removed or its semantics change — for example, if a provider stops returning USD amounts in their cost API. This causes data loss (zero-value metrics) rather than a crash, which is the safer outcome.
- Auth endpoint changes (e.g., Azure AD v1 → v2 migration) cause 401/403 failures with the same alert behavior as a bad credential.

**How to detect provider drift:** watch `llm_exporter_poll_failure_total` for sustained increments on one provider while others remain healthy. Cross-reference with the provider's status page and API changelog. See [docs/failure-modes.md](failure-modes.md) for the full failure-mode matrix.

**When a breaking change ships upstream:** open an issue against this repo. The per-provider HTTP clients are self-contained — a fix is contained to one provider's client file and does not affect others.

## How do `llm.usage.*` OTLP instruments relate to OTel GenAI semantic conventions?

**They are different — intentionally.** The OTel GenAI semantic conventions (`gen_ai.*`) target in-process, per-call SDK instrumentation. `llm.usage.*` instruments aggregate **billing-history API data** — pre-bucketed, delayed, and provider-reported. Using `gen_ai.*` names for billing aggregates would create a semantic collision with SDK-instrumented apps emitting the same metric names with very different freshness and granularity.

Key differences at a glance:

| Concern | `gen_ai.*` (OTel GenAI) | `llm.usage.*` (this exporter) |
|---|---|---|
| Source | In-process SDK calls | Provider billing APIs |
| Freshness | Real-time | Minutes to days of delay |
| Cost metric | None | `llm.usage.cost_usd` |
| `provider` attribute | `gen_ai.system` | `provider` |
| `model` attribute | `gen_ai.request.model` | `model` |

A future optional `OTEL_GENAI_ATTRIBUTE_ALIGNMENT_MODE=true` mode is planned but not shipped yet. It would let backends add or align GenAI attributes for joinability while keeping canonical `llm.usage.*` metrics stable.

Full explanation and a complete mapping table: [docs/otel-compatibility.md](otel-compatibility.md).

## Which FOCUS version does the exporter implement? Is it compatible with FOCUS 1.3?

**The exporter implements FOCUS 1.0 today**, plus one FOCUS 1.1 column (`ServiceSubcategory: "Generative AI"`) backfilled because it is natural to populate and carries no compatibility risk.

Both `/focus.csv` and `/focus.json` include an `X-FOCUS-Version: 1.0` response header so downstream importers can detect the schema version programmatically.

**FOCUS 1.3 support is planned**, with columns `SkuId`, `SkuDescription`, and `ChargeId` (as a deterministic hash). It will be served from separate versioned endpoints (`/focus/v1.3.csv`, `/focus/v1.3.json`) — the existing 1.0 endpoints are frozen to avoid breaking downstream importers that already have a fixed schema. `PricingQuantity` and `ListUnitPrice` are deferred until token counts are available in the cost-bucket data model.

Full column gap analysis and the versioned-endpoint roadmap: [docs/focus-roadmap.md](focus-roadmap.md).

## What happens when something goes wrong — a provider goes down, credentials expire, or the OTLP endpoint is unavailable?

**The exporter is failure-isolated by design.** Two planes of isolation apply:

1. **Per-provider isolation** — a 401/403/429/5xx from one provider does not interrupt polling of other providers. The `CompositeMetricsPublisher` catches per-provider exceptions and continues.
2. **Per-output-plane isolation** — an OTLP write failure does not interrupt Prometheus exposition, and a Prometheus scrape failure does not interrupt OTLP export.

The canonical failure-mode matrix, covering provider 429s, bad credentials, repeated cursors, OTLP endpoint outages, Prometheus scrape failures, checkpoint write failures, and process restarts, is in [docs/failure-modes.md](failure-modes.md). Each scenario includes the immediate effect, the metric signal, the applicable alert, and the recovery path.

For operational runbook steps and symptom → fix patterns, see [docs/troubleshooting.md](troubleshooting.md).

## How do I add a new provider?

Open an issue first describing which provider, what credential model, and which usage / cost APIs are available. Provider addition is a contained PR — see [CONTRIBUTING.md](../CONTRIBUTING.md) for the pattern (one typed HTTP client + one `ILlmUsageProvider` per provider, plus DI extension and tests; the unified `LlmMetricsPublisher` picks up the new `provider` label value automatically).

## Is `/metrics` safe to expose publicly?

No. Treat it as operational data — put it behind your normal internal network controls, mTLS, or an authenticating reverse proxy. The metric values themselves don't include credentials (verified by source review), but exposing your org's cost trend publicly is rarely what you want. See [docs/deployment.md → Step 5](deployment.md#step-5--authenticate-metrics-focuscsv-focusjson) for three patterns (cluster-internal, nginx-ingress basic auth, service mesh / mTLS).

## Can multiple replicas share a `FileCheckpointStore`?

No. The file-backed store is local to a single pod's PVC. For horizontal scale, shard tenants across multiple exporter deployments — each pinned to a tenant subset, each with its own checkpoint PVC. See [docs/deployment.md → Step 7](deployment.md#step-7--high-availability-and-the-multi-replica-caveat).

## Where do FOCUS records go?

They're served at `/focus.csv` and `/focus.json`. The exporter does not push them anywhere — your FinOps tool pulls them on its own cadence. Use them to join LLM spend with cloud spend in tools like Cloudability, Vantage, Apptio, OpenCost, and CloudZero. Concrete S3, SFTP, API relay, ConfigMap, and warehouse import patterns are in [docs/standards.md](standards.md#importing-focus-exports-into-downstream-finops-tools).

## How do I disable a feature I don't want?

| Feature | Disable via |
|---|---|
| A provider | Unset its sentinel env var |
| OTLP | Unset `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Alerts (anomaly + budgets) | `ALERTS_ENABLED=false` |
| FOCUS endpoints | `FOCUS_ENABLED=false` |
| File-backed checkpoints | `CHECKPOINTS_PROVIDER=InMemory` (default) |
