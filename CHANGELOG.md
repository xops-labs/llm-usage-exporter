# Changelog

All notable changes to **llm-usage-exporter** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **Demo mode** (`DEMO_MODE_ENABLED=true`) registers a synthetic `provider="demo"` source that emits deterministic, time-of-day-modulated usage and cost across 5 fake models and 2 fake tenants. Every dashboard lights up immediately, so a developer evaluating the project can preview the shape of the data before involving their platform team for real provider credentials. All series carry `provider="demo"` and obviously-fake model names (`demo-flagship-large`, `demo-fast-mini`, etc.); the startup log emits a warning while it's on. Off by default.

### Changed (breaking — metric namespace consolidation)

**Pre-1.0 breaking change: five per-provider metric prefixes collapse into one unified `llm_*` namespace, with `provider` becoming a label.** Anyone running an earlier pre-release build will need to rewrite any queries / alerts / dashboards that referenced `<provider>_usage_*` or `<provider>_exporter_*`. The repo's shipped dashboards and alerting rules have been updated; downstream consumers must rewrite their own. This is intentionally a clean pre-1.0 break; no compatibility shim is provided.

- **Metric families.** `openai_usage_*`, `azure_openai_usage_*`, `anthropic_usage_*`, `gemini_usage_*`, `bedrock_usage_*` are replaced by a single `llm_usage_*` family carrying a new `provider` label. Same for `*_exporter_*` health metrics → unified `llm_exporter_*`. The complete set: `llm_usage_input_tokens_total`, `llm_usage_output_tokens_total`, `llm_usage_total_tokens_total`, `llm_usage_cached_input_tokens_total`, `llm_usage_requests_total`, `llm_usage_cost_usd_total`, `llm_usage_cost_usd_by_model_total`, `llm_exporter_poll_success_total`, `llm_exporter_poll_failure_total`, `llm_exporter_last_success_timestamp`, `llm_exporter_last_failure_timestamp`, `llm_exporter_last_poll_duration_seconds`.
- **Tenancy label.** The four per-provider tenancy labels (`project_id` for OpenAI / Gemini, `workspace_id` for Anthropic, `resource_id` for Azure OpenAI, `region` for Bedrock) collapse into one unified `tenancy_id` label. The `provider` label disambiguates which kind of ID applies. OTLP tag `tenant_id` is renamed to `tenancy_id` to match.
- **Publishers consolidated.** The five per-provider publisher classes (`OpenAiMetricsPublisher`, `AzureOpenAiMetricsPublisher`, `AnthropicMetricsPublisher`, `GeminiMetricsPublisher`, `BedrockMetricsPublisher`) collapse into a single `LlmMetricsPublisher` that takes the provider name as a constructor parameter. prometheus-net's `IMetricFactory.CreateCounter` is idempotent on (name, label-names), so multiple instances safely share the same metric families.
- **Budget config.** `BudgetDefinition.ProjectIds` renamed to `BudgetDefinition.Tenancies`. The filter now matches against the unified `tenancy_id` label regardless of which provider produced the bucket.
- **Buckets.** `LlmUsageBucket.ProjectId` and `LlmCostBucket.ProjectId` fields renamed to `TenancyId`, with an XML doc explaining the provider-neutral semantics.
- **Dashboards.** All 6 Grafana dashboards rewritten to use the unified metric names. Panel queries now use `{provider="$provider"}` instead of `${provider}_usage_*` interpolation. Template variables (`$provider`, `$tenant`, `$model`) cascade through the new label set.
- **Alerting rules.** 11 rules in `deploy/alerts/llm-usage-exporter.rules.yml` rewritten — `{__name__=~".+_exporter_poll_failure_total"}` regex matchers become direct `llm_exporter_poll_failure_total` references. Checked with `promtool check rules`: `SUCCESS: 11 rules found`.
- **Tests.** Five per-provider publisher test files collapse into one parameterized `LlmMetricsPublisherTests` that covers all five providers; an additional test asserts the central design property that two publisher instances against different providers share metric families and emit distinct provider labels.

### Migration guidance

If you had queries like:

```promql
sum(rate(openai_usage_total_tokens_total[5m]))           # was: per-provider name
```

rewrite as:

```promql
sum(rate(llm_usage_total_tokens_total{provider="openai"}[5m]))
```

Cross-provider aggregations no longer need `__name__` regex tricks:

```promql
sum by (provider) (rate(llm_usage_total_tokens_total[5m]))
```

Budget config moves the projects/workspaces/resources/regions list from per-provider key into the unified `Tenancies` list:

```jsonc
// Before
{ "Name": "...", "ProjectIds": ["proj_abc"] }
// After
{ "Name": "...", "Tenancies": ["proj_abc"] }
```

### Removed

- Deleted `dashboards/grafana-openai-usage-dashboard.json` — the v0.1.0 OpenAI-only legacy dashboard. Its coverage is fully subsumed by the `llm-provider-deep-dive` templated dashboard (pick `openai` from the provider dropdown), which also gains the `tenant` label that the legacy file predates. Keeping it alongside the new `llm-*` set was inconsistent with the project's "consolidated metric surface, provider as a label/namespace" design principle.

## [0.2.0] - 2026-05-13

Pre-1.0 release. Expands the v0.1.0 OpenAI-only exporter into a multi-provider, multi-tenant, supply-chain-hardened FinOps observability tool. Five providers (OpenAI, Azure OpenAI, Anthropic Claude, Google Gemini, AWS Bedrock) on three coordinated output planes (Prometheus, OpenTelemetry OTLP, FOCUS v1.0), with first-class budget burn and anomaly z-score alerting, multi-tenant credential isolation, file-backed checkpoint durability, signed images and SBOMs, and an end-to-end developer guide.

**Highlights**

- Four new providers — Azure OpenAI, Anthropic, Gemini, Bedrock — all with the same canonical metric shape
- OpenTelemetry OTLP export (metrics + traces with W3C `traceparent` propagation)
- FOCUS v1.0 cost-record export at `/focus.csv` and `/focus.json`
- Budget burn and cost / token anomaly z-score Prometheus gauges
- Multi-tenant mode with bearer-token-gated `/metrics?tenant=<id>`
- File-backed checkpoint store for crash-restart durability
- Helm chart with ServiceMonitor, OTLP wiring, alerts, FOCUS, and `extraVolumes` / `extraVolumeMounts`
- Six curated Grafana dashboards covering every Prometheus-visible feature
- 11 Prometheus alerting rules (health, budgets, anomalies), checked by `promtool`
- Cosign keyless signing, SLSA L3 build provenance, CycloneDX + SPDX SBOMs on every release
- DCO-enforced contribution flow
- End-to-end developer guide + production deployment guide + symptom-organized troubleshooting runbook + per-cloud credential setup runbooks
- 86 unit tests (was 33 in v0.1.0)
- Fixed: every provider is now genuinely opt-in in single-tenant mode (the previous build's `.ValidateOnStart()` chain would crash any deployment that wanted only some providers)

### Changed

#### `publishing/` framed honestly as unpublished drafts

As part of public-release preparation, every file under `publishing/` is clearly marked as a draft. The folder previously held marketing and academic material that could be misread as already published, especially `publishing/papers/paper-2.md`, whose abstract called itself an "evaluation plan" but the body used "The results described here support..." language suggesting measurements had been taken.

- Renamed `publishing/papers/` → `publishing/papers-drafts/` (via `git mv`, history preserved) so the folder name signals draft status at a glance.
- New [publishing/README.md](publishing/README.md) frames the entire folder as unpublished templates the maintainer drafts alongside the code, with an explicit "nothing has been submitted, peer reviewed, or accepted" disclaimer at the top and a per-subfolder status table.
- Added a top-of-file `> **Status: unpublished draft**` blockquote to all three paper drafts. `paper-2.md` gets the strongest version, explicitly calling out that no measurements have been collected and that conclusions are conditional on the planned experiments running.
- Fixed `paper-2.md` abstract to remove the misleading "The results described here support four practical conclusions" phrasing — now reads "Once the planned experiments are executed, the expected results would support..." plus a bold "As of this draft no measurements have been performed; the conclusions are predictions grounded in the system's architecture and unit-test coverage."

No functional / shipped-code change. The exporter, its dashboards, alerts, and Helm chart are unaffected.

#### README split into focused reference docs

- The README grew to 931 lines across 20 top-level sections — too much for a GitHub landing page, which most readers scan rather than read. Split the reference material into six focused docs under `docs/` and slimmed the README to 213 lines:

  - [docs/architecture.md](docs/architecture.md) — architecture diagram, principles, engineering conventions, tech stack, features and capabilities, roadmap and build phases, repository layout
  - [docs/metrics.md](docs/metrics.md) — per-provider metric availability table, cross-cutting alert gauges, per-provider metric blocks for all five providers, canonical scrape output
  - [docs/configuration.md](docs/configuration.md) — every environment variable the exporter understands, broken out by feature (exporter, five providers, OTLP, checkpoints, alerts, FOCUS, multi-tenant), plus an example `.env`
  - [docs/provider-apis.md](docs/provider-apis.md) — per-provider HTTP semantics (pagination, auth, retry) for OpenAI, Azure OpenAI, Anthropic, Gemini, Bedrock
  - [docs/standards.md](docs/standards.md) — Prometheus, OpenMetrics, OTLP, W3C Trace Context, FOCUS, SLSA, SBOM, DCO compliance posture
  - [docs/faq.md](docs/faq.md) — frequently asked questions covering what the exporter is, why, how, support, and contributing
- The README now keeps only the landing-page essentials — header, tagline, why-it-matters, six-surface telemetry table, 60-second quickstart, use cases, architecture summary, deployment paths table, and the Documentation Map that links into the rest. A new top-of-document callout points first-time readers at `docs/developer-guide.md` as the canonical start-here walkthrough.
- All in-repo links updated to point at the new files (developer guide and credentials index previously linked to README anchors that no longer exist).

### Fixed

#### Provider opt-in (single-tenant mode)

- Previously, each secondary provider's `.ValidateOnStart()` chain fired unconditionally in single-tenant mode, so any deployment that wanted only OpenAI (or any single provider) would crash at startup with `AZURE_OPENAI_TENANT_ID is required.; AZURE_OPENAI_CLIENT_ID is required.; ...` This contradicted the README's "every provider is independently opt-in" promise.
- Each of the five provider DI extensions (`AddOpenAiProvider`, `AddAzureOpenAiProvider`, `AddAnthropicProvider`, `AddGeminiProvider`, `AddBedrockProvider`) now peeks at `IConfiguration` for its sentinel env var / config key (e.g. `OPENAI_ADMIN_API_KEY`, `AZURE_OPENAI_TENANT_ID`, `ANTHROPIC_ADMIN_API_KEY`, `GEMINI_PROJECT_ID`, `AWS_ACCESS_KEY_ID`) and skips registration entirely in single-tenant mode when nothing is set — no validator, no `HttpClient`, no `LlmProviderRegistration`. Multi-tenant mode is unaffected because it already skipped per-tenant blocks with null provider configs.
- 12 new unit tests in `ProviderOptInTests` (one per provider × env-var path + section path, plus a cross-provider integration test asserting that calling all five `AddXProvider` extensions with only `OPENAI_ADMIN_API_KEY` set yields exactly one `LlmProviderRegistration`).

### Added

#### End-to-end developer guide

- New [docs/developer-guide.md](docs/developer-guide.md) — a ~950-line linear walkthrough from `git clone` to production deployment. 12 numbered steps plus 3 appendices, covering: prerequisites, repo tour, first Docker Compose run, connecting the first real provider, adding more providers, wiring an external Prometheus, **Grafana Cloud integration with both `remote_write` and OTLP patterns** (full walkthrough with the actual endpoint URLs and auth-header format), OTel Collector wiring, alerting rules, budget definitions, multi-tenant mode, Helm-based Kubernetes deployment, and day-2 operations. The appendices cover a configuration cheat sheet, an FAQ pulling out the questions that recur in support channels, and the contributing flow (DCO, tests, schema-stable metrics).
- This is the document a developer landing on the repo should read first — the rest of the docs (README, deployment.md, troubleshooting.md, credentials/) are the reference material it links into.

#### Production deployment guide and operator runbook

- New [docs/deployment.md](docs/deployment.md) — 10-step production deployment guide covering `cosign verify` as a deployment gate, a complete `values-production.yaml`, budget-definitions-via-ConfigMap, three patterns for authenticating `/metrics` (cluster-internal, nginx-ingress + basic auth, service-mesh mTLS), a resource-sizing table, the single-replica / sharding caveat for `FileCheckpointStore`, upgrade and tenant-credential-rotation procedures, and an observability self-monitoring section.
- New [docs/troubleshooting.md](docs/troubleshooting.md) — symptom-organized operator runbook with concrete diagnostic steps for the eight most common failure modes (startup validation crash, one-provider-stuck, all-providers-stuck, cardinality explosion, dashboards empty, budget gauges missing, anomaly z-scores stuck, OTLP backend silent, multi-tenant 401, etc.). Each entry follows a fixed template: "What you'll see", "Diagnostic steps", "Root cause patterns", "Fix".

#### Prometheus alerting rules

- New [deploy/alerts/llm-usage-exporter.rules.yml](deploy/alerts/llm-usage-exporter.rules.yml) with 11 alerts in three rule groups:
  - **`llm-usage-exporter.health`** — `LlmUsageExporterDown`, `LlmUsageExporterUnhealthy`, `LlmUsageExporterPollFailures`, `LlmUsageExporterStale`, `LlmUsageExporterSlowPoll`.
  - **`llm-usage-exporter.budgets`** — `LlmBudgetBurnEarly` (50%), `LlmBudgetBurnHigh` (80%), `LlmBudgetExceeded` (100%).
  - **`llm-usage-exporter.anomalies`** — `LlmCostAnomaly` (±2σ), `LlmCostAnomalySevere` (±3σ), `LlmTokenAnomalySevere` (±3σ).
- Checked with `promtool check rules` (11 rules). Severity ladder is `info` / `warning` / `critical` so on-call routing maps cleanly.
- Brief [deploy/alerts/README.md](deploy/alerts/README.md) covering vanilla-Prometheus and Prometheus-Operator (`PrometheusRule` CRD) wiring, plus a tuning section.

#### OpenTelemetry Collector example

- New [deploy/otel-collector/otel-collector-config.yaml](deploy/otel-collector/otel-collector-config.yaml) — a complete working OTel Collector config that receives OTLP on gRPC (4317) and HTTP (4318), applies `resource` + `batch` + `tail_sampling` processors, and demonstrates four downstream backends (console debug, Prometheus Remote Write, OTLP/HTTP to a managed backend, Jaeger OTLP). Operators pick the exporter(s) matching their stack.
- Brief [deploy/otel-collector/README.md](deploy/otel-collector/README.md) with the local-Docker-run command, Kubernetes wiring notes, verification steps, and a backend-selection table.

#### Per-cloud credential setup runbooks

- New [docs/credentials/](docs/credentials/) directory with step-by-step setup guides for every supported provider — IAM roles, console UI flow, CLI alternative (`gcloud`, `aws`, `az`), env-var / Helm configuration, verification curl, rotation procedure, and common error explanations.
- Five runbooks ship: `openai.md`, `azure-openai.md`, `anthropic.md`, `gemini.md`, `aws-bedrock.md`. The AWS runbook covers both EKS IRSA and IAM-user paths.
- `.env.example` rewritten to mirror the multi-provider README example with comments pointing at each runbook.

#### W3C Trace Context propagation

- `ExporterActivitySource` exposes the `LlmUsageExporter` `ActivitySource` and registers a default `ActivityListener` at process startup so trace activities are produced even when no OTLP exporter is configured. This makes `Activity.Current` non-null while a provider poll is in flight, which lets .NET propagate W3C `traceparent` / `tracestate` headers by default. `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED=false` disables propagation to provider APIs.
- `UsagePollingWorker` wraps each `(tenant, provider)` iteration in a `Client`-kind `Activity` tagged with `llm.provider`, `llm.tenant`, `llm.window.start`, `llm.window.end`, and the usage / cost bucket counts on success — and records the exception type and message as an `ActivityEvent` on failure.
- When OTLP export is enabled, the OTel pipeline now also registers a `WithTracing` provider that listens on both `LlmUsageExporter` and `System.Net.Http`, so the polling spans and their child HTTP spans flow to any OTLP-compatible backend alongside the existing `llm.usage.*` metrics.
- Three new unit tests cover listener registration, `DistributedContextPropagator` injection from an exporter activity, and `Activity.Current` scoping.

#### Supply-chain hardening (SBOMs, SLSA, image signing, DCO)

- The release workflow generates **CycloneDX JSON** SBOMs from the `.NET` project graph using the `CycloneDX` global tool and **SPDX JSON** SBOMs from the published multi-arch container image using Anchore `syft`. Both SBOMs are uploaded as workflow artifacts and attached to every GitHub Release.
- The SPDX SBOM is also pushed to the container registry as a `cosign` attestation (`spdxjson` predicate type) so consumers can resolve it via `cosign verify-attestation` or `cosign download attestation` without hitting GitHub.
- Every published image tag is **signed by `cosign` at its immutable digest** using GitHub OIDC against the Sigstore Fulcio CA — no long-lived signing keys live in the repo.
- A new **SLSA L3 build-provenance** job consumes the [`slsa-framework/slsa-github-generator`](https://github.com/slsa-framework/slsa-github-generator) reusable workflow and pushes signed in-toto SLSA v1.0 provenance attestations to the same registry under the image digest.
- Docker buildx now also embeds `provenance=mode=max` and `sbom=true` into the OCI image manifest itself, so `docker buildx imagetools inspect` exposes both natively.
- A new **DCO** workflow runs on every PR and rejects merges unless every non-merge commit in the PR carries a `Signed-off-by:` trailer matching its commit author. `GOVERNANCE.md` and `CONTRIBUTING.md` are updated to reflect that DCO is enforced and that no separate CLA is required.

#### Multi-provider expansion

The exporter now ships four additional first-class LLM providers alongside OpenAI, each as an independent `ILlmUsageProvider` registered into the shared polling worker. Every provider keeps its own Prometheus metric namespace, fails in isolation if its credentials are missing, and emits the same canonical `*_usage_*` and `*_exporter_*` metric shape.

- **Azure OpenAI provider** — pulls per-deployment token counts (`ProcessedPromptTokens`, `GeneratedTokens`, `TokenTransaction`) from the Azure Monitor metrics REST API and per-resource spend from the Azure Cost Management query API. Auth uses Azure AD client-credentials with a cached bearer token. Emits `azure_openai_usage_*` and `azure_openai_exporter_*` metrics labelled by `model` (deployment name) and `resource_id`.
- **Anthropic Claude provider** — pulls organization-scoped usage from `/v1/organizations/usage_report/messages` and spend from `/v1/organizations/cost_report` with cursor pagination. Tracks uncached, cached, and cache-creation input tokens separately so prompt-caching dashboards work the same way they do for OpenAI. Emits `anthropic_usage_*` metrics labelled by `model` and `workspace_id`.
- **Google Gemini provider** — pulls Vertex AI token counts from Cloud Monitoring's `timeSeries.list` API (separate queries for input tokens, output tokens, and request count, joined per `(startTime, endTime, model_id)`) and optionally pulls Gemini spend from BigQuery billing export when `Gemini:EnableCostQueries` is on. Auth accepts either a static access token or a service-account keyfile exchanged via a hand-rolled JWT-bearer flow — no Google SDK dependency. Emits `gemini_usage_*` metrics labelled by `model` and `project_id`.
- **AWS Bedrock provider** — pulls invocation metrics (`InputTokenCount`, `OutputTokenCount`, `Invocations`) from CloudWatch `GetMetricData` and Bedrock spend from Cost Explorer `GetCostAndUsage` filtered to `Amazon Bedrock`. Requests are signed inline with AWS SigV4 using `System.Security.Cryptography` — no AWS SDK dependency. Emits `bedrock_usage_*` metrics labelled by `model` and `region`.

#### Shared provider scaffolding

- `ILlmMetricsPublisher` abstraction so every provider can own its own Prometheus surface.
- `LlmProviderRegistration` record bundling a provider with its paired publisher under a stable name.
- `UsagePollingWorker` now iterates every registered `LlmProviderRegistration` independently — a failure in one provider no longer prevents the others from polling.
- Per-provider `AddXProvider(IConfiguration)` DI extension methods keep `Program.cs` to a single one-line registration per provider.

#### Environment variables

- Azure OpenAI: `AZURE_OPENAI_TENANT_ID`, `AZURE_OPENAI_CLIENT_ID`, `AZURE_OPENAI_CLIENT_SECRET`, `AZURE_OPENAI_SUBSCRIPTION_ID`, `AZURE_OPENAI_RESOURCE_GROUP`, `AZURE_OPENAI_ACCOUNT_RESOURCE_IDS`, `AZURE_OPENAI_MODELS`.
- Anthropic: `ANTHROPIC_ADMIN_API_KEY`, `ANTHROPIC_VERSION`, `ANTHROPIC_WORKSPACE_IDS`, `ANTHROPIC_MODELS`, `ANTHROPIC_API_KEY_IDS`.
- Gemini: `GEMINI_PROJECT_ID`, `GEMINI_BILLING_PROJECT_ID`, `GEMINI_BILLING_DATASET_PROJECT`, `GEMINI_BILLING_DATASET_ID`, `GEMINI_BILLING_TABLE`, `GEMINI_ENABLE_COST_QUERIES`, `GEMINI_ACCESS_TOKEN`, `GEMINI_SERVICE_ACCOUNT_KEY_FILE`, `GEMINI_MODELS`.
- Bedrock: `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN`, `AWS_REGION` (or `BEDROCK_REGION`), `BEDROCK_MODEL_IDS`, `BEDROCK_ENABLE_COST_QUERIES`.

#### OpenTelemetry OTLP export

- `LlmMeterPublisher` emits canonical `llm.usage.*` counters and observable gauges (input/output/total tokens, requests, USD cost) tagged with `provider`, `model`, `tenant_id`, and `tenant` — published to any OTLP gRPC or HTTP endpoint configured via `OTEL_EXPORTER_OTLP_ENDPOINT`.
- `CompositeMetricsPublisher` wraps each provider's prometheus-net publisher together with the meter publisher and forwards every call with per-publisher exception isolation, so an OTLP backend outage cannot stop the Prometheus surface.
- The five provider DI extensions resolve the composite only when `LlmMeterPublisher` is registered, so the default config keeps the existing prometheus-only path.

#### Kubernetes Helm chart

- Ships at `deploy/helm/llm-usage-exporter/` with Deployment, Service, ConfigMap, Secret, ServiceMonitor, ServiceAccount, and a separate Secret for the Gemini service-account keyfile.
- Each provider is opt-in via its `enabled` flag in `values.yaml`; only the enabled providers' env vars are rendered into the ConfigMap and Secret.
- Optional Prometheus-Operator `ServiceMonitor` is gated on both `serviceMonitor.enabled` and the `monitoring.coreos.com/v1` CRD being present, so the chart renders cleanly on stock clusters too.
- Pod runs as non-root with a read-only root filesystem and ALL capabilities dropped.

#### Durable checkpoint storage

- New `ICheckpointStore` abstraction replaces the per-publisher in-memory `HashSet<string>` dedupe.
- `InMemoryCheckpointStore` (default) preserves the existing semantics for dev runs and tests.
- `FileCheckpointStore` persists every recorded bucket identity as a JSON-lines record (`./data/checkpoints.jsonl` by default), reloads them on startup, periodically flushes via `CheckpointFlushHostedService`, and compacts the file when `MaxEntries` is exceeded — keeping only entries newer than `RetentionHours`.
- Identity format (`provider|startTs|endTs|model|projectId|metricType`) is unchanged — existing checkpoint files remain readable.

#### Multi-tenant mode

- New `Tenants` config section: `Items` is a list of per-tenant credential blocks, one block per provider. When `Items` is empty (default), the exporter runs in its existing single-tenant mode.
- Each provider's DI extension registers one `LlmProviderRegistration` per `(tenant, provider)` pair with an isolated HttpClient, options instance, and metrics publisher — failures in one tenant do not affect others.
- Every Prometheus counter/gauge gains a `tenant` label as its first label (single-tenant runs emit `tenant="default"` for backward compat).
- OTLP `llm.usage.*` instruments add a `tenant` attribute; FOCUS records carry `Tenant` and `x_tenant_id` columns; checkpoint dedupe identities are scoped per tenant.
- `/metrics?tenant=<id>` filters the exposition to a single tenant. When `Tenants.ApiKeys` is configured, the filter requires `Authorization: Bearer <token>` matching the tenant's token (returns 401 otherwise).
- `BudgetDefinition.Tenants` filter scopes budgets to specific tenants; `AnomalyDetector` keys its rolling window by `(tenant, provider, model)`.

#### Anomaly + budget alerting

- New Alerts module evaluates two signals over the existing bucket stream and publishes them as alertable Prometheus gauges.
- **Budgets** — per-budget burn ratio (`llm_alerts_budget_burn_ratio{tenant,budget_name,...}`) plus supporting `llm_alerts_budget_spend_usd`, `llm_alerts_budget_limit_usd`, and `llm_alerts_budget_period_start_timestamp`. Periods are monthly / weekly / daily; filters on provider, model, project, and tenant.
- **Cost and token anomaly scores** — per-`(tenant, provider, model)` rolling z-score clamped to ±10, published as `llm_alerts_cost_anomaly_score` and `llm_alerts_token_anomaly_score`.
- Wired via a new `IRegistrationDecorator` abstraction that wraps each provider's publisher with an `AlertObservingPublisher`. The `AlertEvaluator` is a hosted service that drains an in-memory `IAlertSource` on a configurable interval.

#### FOCUS specification export

- New `/focus.csv` and `/focus.json` endpoints serve accumulated cost records in the [FOCUS v1.0](https://focus.finops.org/) FinOps Foundation schema — joinable with cloud-cost data in tools like Cloudability, Apptio, Vantage, OpenCost.
- Every record carries the full 32-column FOCUS surface (`ChargePeriodStart`, `BilledCost`, `EffectiveCost`, `ListCost`, `ServiceName`, `ServiceCategory="AI and Machine Learning"`, `ServiceSubcategory="Generative AI"`, `Tenant`, etc.) plus `x_provider_native_id` and `x_tenant_id` extension columns.
- `InMemoryFocusRecordStore` is bounded (default 50k records, oldest dropped) and thread-safe; `FocusObservingPublisher` decorator forwards calls to the inner publisher and pushes mapped records to the store on `PublishCosts`.

#### Environment variables

- OTel: `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_SERVICE_NAME`, `OTEL_SERVICE_VERSION`, `OTEL_METRIC_EXPORT_INTERVAL_SECONDS`.
- Checkpoints: `CHECKPOINTS_PROVIDER` (`InMemory` | `File`), `CHECKPOINTS_FILE_PATH`, `CHECKPOINTS_MAX_ENTRIES`, `CHECKPOINTS_RETENTION_HOURS`, `CHECKPOINTS_FLUSH_INTERVAL_SECONDS`.
- Alerts: `ALERTS_ENABLED`, `ALERTS_EVALUATION_INTERVAL_SECONDS`, `ALERTS_ROLLING_WINDOW_BUCKETS`.
- FOCUS: `FOCUS_ENABLED`, `FOCUS_MAX_RECORDS`.

#### Dependencies

- Added `OpenTelemetry.Extensions.Hosting` 1.15.3 and `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.3 (pinned at this version to clear the NU1902 advisories that hit 1.10.x and 1.13.x). No other NuGet packages added to the API project. Tests project adds `Microsoft.AspNetCore.Mvc.Testing` 10.0.0 for the FOCUS endpoint tests.

### Changed

- `LlmProviderRegistration` is now a record with a `Tenant` init property (default `TenantContext.Default`) so existing positional construction stays compatible.
- `LlmUsageBucket` and `LlmCostBucket` gain a `Tenant` init property (default `"default"`). Existing positional construction is unchanged.
- All five provider metrics publishers add `tenant` as the first label on every counter/gauge. Single-tenant scrapes emit `tenant="default"`.
- `AnomalyDetector` rolling-window key changed from `(provider, model)` to `(tenant, provider, model)` — `AlertEvaluator` and `AlertMetricsPublisher` updated accordingly.

### Tests

- 86 tests total, all passing. Coverage spans every provider's response mapping, retry/transient-error behaviour, metric-publisher dedupe with the new tenant label, AWS SigV4 signer regression, OTel meter recording, checkpoint persistence + compaction, budget evaluation across periods + filters, anomaly z-score clamping, FOCUS record mapping + endpoint output, multi-tenant DI registration, `/metrics?tenant=<id>` filtering, bearer-token auth on tenant-scoped scrapes, ActivitySource listener registration, `DistributedContextPropagator` traceparent injection, `Activity.Current` scoping during a poll iteration, and the new provider opt-in guard across all five providers.

---

## [0.1.0] - 2026-05-12

### Added

#### Exporter core
- Initial public release of `llm-usage-exporter`, an open-source Prometheus exporter for LLM cost, token usage, and AI spend observability.
- OpenAI organization Usage and Costs API polling with in-memory bucket deduplication and a configurable rolling-lookback window.
- Prometheus `/metrics` endpoint at port 8080 exposing:
  - `openai_usage_input_tokens_total{model,project_id}`
  - `openai_usage_output_tokens_total{model,project_id}`
  - `openai_usage_total_tokens_total{model,project_id}`
  - `openai_usage_cached_input_tokens_total{model,project_id}`
  - `openai_usage_requests_total{model,project_id}`
  - `openai_usage_cost_usd_total{project_id}`
  - `openai_usage_cost_usd_by_model_total{model,project_id}`
  - `openai_exporter_poll_success_total`
  - `openai_exporter_poll_failure_total`
  - `openai_exporter_last_success_timestamp`
  - `openai_exporter_last_failure_timestamp`
  - `openai_exporter_last_poll_duration_seconds`
- `/health` endpoint backed by `ExporterHealthState` for liveness and data-freshness probes.
- Environment-variable configuration: `OPENAI_ADMIN_API_KEY`, `OPENAI_ORG_ID`, `OPENAI_PROJECT_IDS`, `EXPORTER_POLL_INTERVAL_SECONDS`, `EXPORTER_LOOKBACK_MINUTES`, `EXPORTER_GROUP_BY`.
- Low-cardinality-by-default label policy: `user_id` and API-key labels are intentionally excluded.

#### Deployment & dashboards
- Docker Compose stack at `deploy/docker-compose.yml` bundling the exporter with a pre-configured Prometheus scrape target.
- Starter Grafana dashboard at `dashboards/grafana-openai-usage-dashboard.json`.
- Multi-stage, multi-arch Dockerfile (linux/amd64 + linux/arm64).

#### Community & OSS scaffolding
- Apache-2.0 license with `NOTICE`, `AUTHORS.md` attribution.
- `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md` for community standards and private disclosure.
- `MAINTAINERS.md` with maintainer table, role ladder (Contributor → Committer → Maintainer → Project lead), nomination process, and inactivity policy.
- `GOVERNANCE.md` documenting lazy-consensus decision making, a 72-hour RFC-lite window for breaking changes, conflict-of-interest policy, and a deferral path to heavyweight governance.
- `SUPPORT.md` routing users to Discussions, Issues, or private security disclosure.
- Marketing-forward, SEO-rich `README.md` with hero block, two badge rows, "Built for" personas, key-capabilities cascade, six-surface observation table, Mermaid architecture diagram with provider and roadmap edges, 23-row Features & Capabilities table phased 1–5, expanded use cases, Standards & Compliance sub-tables (metrics / FinOps / supply-chain), Tech Stack table, Repository Layout tree, five-phase roadmap, twelve-entry FAQ, and a dense keyword-tag list for GitHub and search-engine discoverability.

#### GitHub repo scaffolding
- Structured YAML issue forms: `bug_report.yml`, `feature_request.yml`, plus `config.yml` that disables blank issues and routes users to Discussions and private security reporting.
- Pull-request template capturing summary, test plan, and metric/config-surface changes.
- `CODEOWNERS` for auto-assigned reviews.
- `FUNDING.yml` placeholder for future sponsorship surfaces.
- Dependabot config for weekly, grouped NuGet, GitHub Actions, and Docker base-image updates.
- Auto-generated release-notes configuration (`.github/release.yml`) categorizing merged PRs by the project label taxonomy.

#### CI/CD & supply chain
- GitHub Actions CI workflow building and testing the solution on Ubuntu and Windows with the .NET 10 SDK (GA channel), plus a Docker image build and a community-files sanity check.
- CodeQL security scanning workflow covering `csharp` and `actions` languages on push, on pull request, and weekly, with the `security-extended` and `security-and-quality` query packs.
- Tag-driven release workflow at `.github/workflows/release.yml` that publishes multi-arch container images (linux/amd64 + linux/arm64) to GHCR with semver + `latest` tags and creates a GitHub Release whose body is extracted from the matching `CHANGELOG.md` section. Prereleases are detected automatically from tags containing a hyphen.

#### Developer experience
- `.editorconfig` with .NET-aware Roslyn style rules (file-scoped namespaces, expression-bodied members, var preferences, modifier order), 2-space YAML/JSON/MSBuild, and CRLF for PowerShell scripts.
- `.gitattributes` normalizing line endings to LF across platforms, marking binaries, and applying Linguist hints to keep dashboard JSON out of the repo's language statistics.

### Security
- No known security fixes in this release (initial public release).
- Private vulnerability disclosure policy published in `SECURITY.md`.
- CodeQL scanning enabled from day one.

[Unreleased]: https://github.com/xops-labs/llm-usage-exporter/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/xops-labs/llm-usage-exporter/releases/tag/v0.1.0
