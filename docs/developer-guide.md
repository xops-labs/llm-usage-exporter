# Developer guide — `llm-usage-exporter`

A linear, end-to-end walkthrough for someone who just landed on this repo and wants to get from `git clone` to "running in my production cluster with Grafana Cloud dashboards and Alertmanager paging me on budget burn". Each section answers the practical questions a developer hits at that step.

Reference docs (this guide links to them as you reach the relevant step):

- [README.md](../README.md) — overview, metric catalog, env-var reference
- [docs/credentials/](credentials/) — per-cloud credential setup (one runbook per provider)
- [docs/deployment.md](deployment.md) — production Helm deployment, sizing, auth, HA
- [docs/troubleshooting.md](troubleshooting.md) — symptom-organized runbook
- [deploy/helm/llm-usage-exporter/README.md](../deploy/helm/llm-usage-exporter/README.md) — Helm chart values reference
- [deploy/alerts/README.md](../deploy/alerts/README.md) — Prometheus alerting rules
- [deploy/otel-collector/README.md](../deploy/otel-collector/README.md) — OTel Collector wiring

---

## Table of contents

1. [Before you start](#1-before-you-start)
2. [Prerequisites](#2-prerequisites)
3. [Step 1 — Clone the repo and tour the layout](#step-1--clone-the-repo-and-tour-the-layout)
4. [Step 2 — First run with Docker Compose](#step-2--first-run-with-docker-compose)
5. [Step 3 — Connect your first real provider](#step-3--connect-your-first-real-provider)
6. [Step 4 — Add more providers](#step-4--add-more-providers)
7. [Step 5 — Wire your own Prometheus](#step-5--wire-your-own-prometheus)
8. [Step 6 — Wire Grafana (self-hosted or Grafana Cloud)](#step-6--wire-grafana-self-hosted-or-grafana-cloud)
9. [Step 7 — Wire OpenTelemetry (optional)](#step-7--wire-opentelemetry-optional)
10. [Step 8 — Configure alerts](#step-8--configure-alerts)
11. [Step 9 — Set up budgets](#step-9--set-up-budgets)
12. [Step 10 — Multi-tenant deployment](#step-10--multi-tenant-deployment)
13. [Step 11 — Production deployment (Kubernetes / Helm)](#step-11--production-deployment-kubernetes--helm)
14. [Step 12 — Day-2 operations](#step-12--day-2-operations)
15. [Appendix A — Configuration quick reference](#appendix-a--configuration-quick-reference)
16. [Appendix B — Common questions](#appendix-b--common-questions)
17. [Appendix C — Contributing back](#appendix-c--contributing-back)

---

## 1. Before you start

`llm-usage-exporter` is a single-binary Prometheus + OpenTelemetry exporter that polls **five LLM providers** — OpenAI, Azure OpenAI, Anthropic Claude, Google Gemini, AWS Bedrock — for organization-scoped token usage and USD cost data, then republishes it as:

- **Prometheus metrics** at `/metrics` (the primary surface)
- **OpenTelemetry OTLP** for metrics and traces with W3C `traceparent` propagation enabled by default
- **FOCUS v1.0** cost records at `/focus.csv` and `/focus.json` for FinOps tools

Run it next to your existing Prometheus / Grafana / OTel collector and you get a near-real-time operational signal for per-model, per-tenant token + USD telemetry — no SaaS, no agents, no proprietary collectors. Apache-2.0, self-hosted.

**Who this is for:** platform engineers, SREs, FinOps practitioners, AI platform leads who own a Prometheus stack and want LLM cost on the same dashboards as latency and error rate.

**What it isn't:** a full billing system, a prompt-level observability product, or a replacement for the providers' own invoices. It's a near-real-time operational signal that surfaces cost movement *while you can still do something about it*.

---

## 2. Prerequisites

You need **one** of these toolchains to follow the local-trial path:

| Toolchain | What for | How to check |
|---|---|---|
| **Docker + Docker Compose** (recommended) | Local development stack | `docker --version && docker compose version` |
| **.NET 10 SDK** | Run `dotnet run` instead of Docker | `dotnet --list-sdks` should show 10.0.x. The repo pins via [`global.json`](../global.json). |

For production / Kubernetes you'll additionally want:

- **`kubectl`** — to talk to your cluster
- **`helm` 3.x** — to install the chart
- **`cosign`** (optional but recommended) — to verify the image signature before deploying

And you need **at least one provider credential** to see real data. The exporter starts cleanly without any credentials (it just sits idle), but to do anything useful you need to point it at a real provider. Pick the one your team uses today and grab the credential per [`docs/credentials/`](credentials/) — it's the same set of permissions you'd grant any read-only billing dashboard.

---

## Step 1 — Clone the repo and tour the layout

```bash
git clone https://github.com/xops-labs/llm-usage-exporter.git
cd llm-usage-exporter
```

Top-level layout you'll actually touch:

```
llm-usage-exporter/
├── .env.example              ← copy this to .env in Step 2
├── src/                      ← .NET source (Program.cs, providers, alerts, OTLP, ...)
├── deploy/
│   ├── docker-compose.yml    ← brings up exporter + Prometheus + Grafana
│   ├── prometheus.yml        ← scrape config used by the local Prometheus
│   ├── grafana/provisioning/ ← auto-wires the dashboards into local Grafana
│   ├── alerts/               ← Prometheus alerting rules (drop into your own Prometheus)
│   ├── otel-collector/       ← worked OTel Collector config
│   └── helm/llm-usage-exporter/ ← the Helm chart for Kubernetes
├── dashboards/               ← 6 Grafana dashboards (auto-loaded in Step 2)
├── docs/
│   ├── credentials/          ← per-cloud credential setup runbooks
│   ├── deployment.md         ← production deployment guide
│   └── troubleshooting.md    ← symptom-organized runbook
└── tests/                    ← 86 unit tests
```

You won't need to edit any source code to run the exporter. Everything is configuration.

---

## Step 2 — First run with Docker Compose

The repo ships with a docker-compose file that brings up three containers: the exporter, a Prometheus that scrapes it, and a Grafana with dashboards auto-imported. This is the fastest way to confirm the moving parts work.

### 2.1 Make an `.env` (even a stub one)

```bash
cp .env.example .env
```

You can leave it as-is (every provider unset → no providers register → exporter starts idle and serves empty `/metrics`) or you can drop in a real `OPENAI_ADMIN_API_KEY` to get real data immediately. **Every provider in the exporter is independently opt-in** — leave a provider's required variables unset and that provider is not registered. So a single-line `.env` containing only `OPENAI_ADMIN_API_KEY=sk-admin-...` works.

> **No provider credentials yet?** Add `DEMO_MODE_ENABLED=true` to your `.env`. The exporter registers a synthetic `provider="demo"` source that emits deterministic, time-of-day-modulated usage and cost across 5 fake models and 2 fake tenants — every dashboard lights up immediately so you can evaluate the shape of the data before involving your platform team for real credentials. All series carry `provider="demo"` and obviously-fake model names (`demo-flagship-large`, `demo-fast-mini`, …); turn it off for any production deployment.

### 2.2 Bring up the stack

```bash
docker compose -f deploy/docker-compose.yml up --build -d
```

First build takes ~60 seconds. On subsequent runs the image is cached and it starts in ~5 seconds.

### 2.3 Open the URLs

| URL | Purpose | Expected response |
|---|---|---|
| http://localhost:8080/health | Exporter liveness | 200 OK with `{"status":"healthy",...}` |
| http://localhost:8080/metrics | Prometheus exposition | Plain-text metrics |
| http://localhost:8080/focus.json | FOCUS v1.0 records | `[]` until polls succeed |
| http://localhost:8080/focus.csv | Same data, CSV | Header row + rows |
| http://localhost:9090 | Prometheus UI | Targets page should show `llm-usage-exporter` as **UP** |
| http://localhost:3000 | Grafana | Login `admin` / `admin` |

In Grafana, the **LLM Usage Exporter** folder contains six auto-provisioned dashboards:

- **LLM Usage — Overview** — single pane of glass
- **LLM Usage — Cost & Budgets** — FinOps view + budget burn + anomaly scores
- **LLM Usage — Tokens & Caching** — token volume + prompt-caching ROI
- **LLM Usage — Exporter Health** — per-provider poll success / failure
- **LLM Usage — Multi-Tenant** — per-tenant breakdown
- **LLM Usage — Provider Deep Dive** — pick any provider from a dropdown

If you don't have real credentials yet, the dashboards will show exporter-health panels with live data and usage panels empty. That's expected.

### 2.4 Tear down

```bash
docker compose -f deploy/docker-compose.yml down -v
```

`-v` removes the named volumes (Prometheus TSDB, Grafana SQLite). Drop it if you want to preserve state between runs.

### 2.5 Optional — run without Docker

If you have .NET 10 installed:

```bash
$env:OPENAI_ADMIN_API_KEY="sk-admin-replace-me"
dotnet run --project src/LlmUsageExporter.Api
```

Same `/health`, `/metrics`, `/focus.*` surface — just without Prometheus and Grafana around it.

---

## Step 3 — Connect your first real provider

Pick the provider your team actually uses. The exporter's strength is that every provider is the same — once you've wired one, the next four are mechanical.

### 3.1 Get the credential

Each provider has a dedicated runbook with step-by-step UI clicks, CLI commands, the exact IAM permissions, and a verification curl:

| Provider | Runbook | Sentinel env var |
|---|---|---|
| OpenAI | [docs/credentials/openai.md](credentials/openai.md) | `OPENAI_ADMIN_API_KEY` |
| Azure OpenAI | [docs/credentials/azure-openai.md](credentials/azure-openai.md) | `AZURE_OPENAI_TENANT_ID` |
| Anthropic | [docs/credentials/anthropic.md](credentials/anthropic.md) | `ANTHROPIC_ADMIN_API_KEY` |
| Google Gemini | [docs/credentials/gemini.md](credentials/gemini.md) | `GEMINI_PROJECT_ID` |
| AWS Bedrock | [docs/credentials/aws-bedrock.md](credentials/aws-bedrock.md) | `AWS_ACCESS_KEY_ID` |

The runbooks also include the exact IAM policies / minimum permissions — **the exporter only needs read-only access to organization usage / cost APIs**. Never give it data-plane (model invocation) permissions.

### 3.2 Verify the credential works *against the provider* before pointing the exporter at it

This is the single most useful debugging step and the one most people skip. Every runbook ends with a curl command that hits the upstream's actual API. If that curl returns 200, the credential is good. If it returns 401 / 403, fix that *before* you touch the exporter.

Example (OpenAI):

```bash
curl -sS https://api.openai.com/v1/organizations/usage/completions \
  -H "Authorization: Bearer $OPENAI_ADMIN_API_KEY" \
  -G --data-urlencode "start_time=$(($(date +%s) - 3600))"
```

### 3.3 Add it to your `.env`

```bash
echo "OPENAI_ADMIN_API_KEY=sk-admin-xxxxxxxxxxxxxxxxxxxxxxxx" >> .env
```

### 3.4 Restart the exporter

```bash
docker compose -f deploy/docker-compose.yml restart exporter
```

### 3.5 Confirm polling

```bash
# Should show a non-zero success counter after ~5 minutes (default poll interval)
curl -s http://localhost:8080/metrics | grep 'llm_exporter_poll_success_total{.*provider="openai"'

# Should show real token counts shortly after
curl -s http://localhost:8080/metrics | grep 'llm_usage_input_tokens_total{.*provider="openai"'
```

In Grafana, open **LLM Usage — Overview** — within one poll interval you should see real spend / tokens / requests appearing.

---

## Step 4 — Add more providers

Same pattern, one section at a time. Every provider you skip is a provider that doesn't register — no validation, no metrics, no polling overhead.

Quick reference — minimum env vars for each provider (full setup in [docs/credentials/](credentials/)):

```bash
# OpenAI
OPENAI_ADMIN_API_KEY=sk-admin-...

# Azure OpenAI
AZURE_OPENAI_TENANT_ID=00000000-0000-0000-0000-000000000000
AZURE_OPENAI_CLIENT_ID=00000000-0000-0000-0000-000000000000
AZURE_OPENAI_CLIENT_SECRET=secret
AZURE_OPENAI_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000
AZURE_OPENAI_ACCOUNT_RESOURCE_IDS=/subscriptions/.../accounts/myaoai

# Anthropic
ANTHROPIC_ADMIN_API_KEY=sk-ant-admin-...

# Gemini (one of two auth modes)
GEMINI_PROJECT_ID=my-gcp-proj
GEMINI_SERVICE_ACCOUNT_KEY_FILE=/secrets/gemini-sa.json
# OR
GEMINI_ACCESS_TOKEN=$(gcloud auth print-access-token)

# AWS Bedrock
AWS_ACCESS_KEY_ID=AKIA...
AWS_SECRET_ACCESS_KEY=...
AWS_REGION=us-east-1
```

After each addition, restart the exporter and confirm:

```bash
docker compose -f deploy/docker-compose.yml restart exporter
docker compose -f deploy/docker-compose.yml logs exporter | grep "Starting usage polling worker"
# → Starting usage polling worker for providers: default/openai, default/anthropic, default/azure_openai, ...
```

Behind the scenes, the canonical metric shape stays identical across providers — the same dashboards work without modification.

### What if one provider fails?

Per-provider isolation: a 401 / 403 / 500 on one provider does not stop the others. Each provider polls and authenticates independently. `/health` returns 503 only after the *aggregate* failure threshold (default 3 consecutive failed polls) is crossed. Look at `llm_exporter_poll_failure_total{provider="<name>"}` to identify the specific failing provider.

---

## Step 5 — Wire your own Prometheus

The compose stack runs its own Prometheus on port 9090, which is fine for local trial. For your real Prometheus instance:

### 5.1 If you run vanilla Prometheus

Add a scrape job to your `prometheus.yml`:

```yaml
scrape_configs:
  - job_name: "llm-usage-exporter"
    scrape_interval: 30s
    static_configs:
      - targets: ["llm-usage-exporter.observability.svc.cluster.local:8080"]
```

Reload Prometheus (`SIGHUP` or `POST /-/reload`). Confirm in **Status → Targets** that `llm-usage-exporter` is **UP**.

### 5.2 If you run Prometheus Operator (kube-prometheus-stack)

The Helm chart can render a `ServiceMonitor`:

```yaml
serviceMonitor:
  enabled: true
  interval: 30s
  scrapeTimeout: 10s
  labels:
    release: kube-prometheus-stack   # match your Prometheus instance's selector
```

Apply via `helm upgrade --install` (see Step 11). The Prometheus operator discovers it automatically.

### 5.3 If you want long-term storage

For multi-month retention, push to a Prometheus-compatible TSDB via `remote_write` — Mimir, VictoriaMetrics, Cortex, Thanos, Grafana Cloud, Amazon AMP. Configure on the Prometheus side, not the exporter:

```yaml
remote_write:
  - url: https://prometheus-prod-XX-prod-us-central-0.grafana.net/api/prom/push
    basic_auth:
      username: <stack-id>
      password: <api-token>
```

The exporter doesn't know or care that you're remote-writing.

### 5.4 What to scrape

The exporter exposes three scrape surfaces — all are operational data, never expose them publicly:

| Path | Purpose |
|---|---|
| `/metrics` | Prometheus exposition |
| `/metrics?tenant=<id>` | Multi-tenant filter (optional bearer-token gating) |
| `/health` | Liveness — 200 healthy / 503 unhealthy |

`/focus.csv` and `/focus.json` are FOCUS records, not Prometheus metrics — scrape those separately if you push them into a FinOps tool.

---

## Step 6 — Wire Grafana (self-hosted or Grafana Cloud)

The local compose stack runs Grafana with the dashboards already provisioned. To get them into your real Grafana instance:

### 6.1 Self-hosted Grafana — drop the JSON in

**Manual import:**
- Grafana → **Dashboards → New → Import → Upload JSON file**
- Pick each file under [dashboards/](../dashboards/)
- Grafana prompts for the `${DS_PROMETHEUS}` template variable — select your Prometheus datasource

**Automated provisioning** (recommended):
- Mount [deploy/grafana/provisioning/](../deploy/grafana/provisioning/) into your Grafana container at `/etc/grafana/provisioning`
- Mount the [dashboards/](../dashboards/) folder at `/var/lib/grafana/dashboards`
- The provider config under `dashboards/llm-usage-exporter.yml` auto-imports everything on startup

### 6.2 Grafana Cloud — the full walkthrough

Grafana Cloud is the SaaS Grafana with managed Mimir for metrics and managed Tempo for traces. It's the most common production target after self-hosted Grafana.

Two integration patterns. Pick one or both:

#### Pattern A — Push Prometheus metrics to Grafana Cloud Metrics

Your local Prometheus scrapes the exporter, then remote_writes to Grafana Cloud Mimir. The exporter itself doesn't change.

**Step 1: Get your Grafana Cloud Metrics push endpoint.**

In Grafana Cloud → **Connections → Add new connection → Hosted Prometheus metrics** (or **My Account → Stack → Prometheus → Send Metrics**). Copy:
- The **Remote write endpoint** URL (something like `https://prometheus-prod-XX-prod-us-central-0.grafana.net/api/prom/push`)
- The **Username** (your numeric stack ID)
- A **Cloud Access Policy token** with `metrics:write` scope

**Step 2: Configure your Prometheus.**

```yaml
remote_write:
  - url: https://prometheus-prod-XX-prod-us-central-0.grafana.net/api/prom/push
    basic_auth:
      username: "1234567"           # your stack ID
      password: "glc_XXXXXXXXXX"    # the access policy token
    queue_config:
      capacity: 10000
      max_shards: 5
      max_samples_per_send: 1000
    write_relabel_configs:
      # Only push the llm-usage-exporter series, not your whole stack
      - source_labels: [__name__]
        regex: "(llm_usage_.*|llm_exporter_.*|llm_alerts_.*|up)"
        action: keep
      - source_labels: [job]
        regex: "llm-usage-exporter"
        action: keep
```

The `write_relabel_configs` block is optional but important if you're billing by series count — Grafana Cloud charges per active series.

**Step 3: Verify in Grafana Cloud.**

Log into your Grafana Cloud Grafana → **Explore → Prometheus → Metrics browser**, type `llm_usage_` and you should see the exporter's series.

**Step 4: Import the dashboards.**

Same import flow as self-hosted Grafana — but select the Grafana Cloud Mimir datasource (it's pre-configured as `grafanacloud-<stack>-prom`).

#### Pattern B — Push OTLP directly from the exporter to Grafana Cloud

Skip Prometheus entirely (or run them side by side). The exporter sends OTLP metrics + traces directly to Grafana Cloud's OTLP gateway.

**Step 1: Get your Grafana Cloud OTLP endpoint.**

In Grafana Cloud → **Connections → Add new connection → OpenTelemetry (OTLP)**. Copy:
- The **OTLP gRPC endpoint** (e.g. `https://otlp-gateway-prod-us-central-0.grafana.net/otlp`)
- Your **Instance ID** (numeric stack ID, used as username)
- A **Cloud Access Policy token** with `metrics:write` + `traces:write` scopes

Grafana Cloud expects basic-auth credentials encoded into the `Authorization` header — concatenate as `<stackId>:<token>` and base64-encode:

```bash
echo -n "1234567:glc_XXXXXXXXXX" | base64
# eDIzNDU2NzpnbGNfWFhYWFhYWFhYWA==
```

**Step 2: Configure the exporter.**

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-us-central-0.grafana.net/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=authorization=Basic eDIzNDU2NzpnbGNfWFhYWFhYWFhYWA==
OTEL_SERVICE_NAME=llm-usage-exporter
```

For Helm, the same goes into `values.yaml`:

```yaml
otel:
  enabled: true
  endpoint: https://otlp-gateway-prod-us-central-0.grafana.net/otlp
  protocol: http/protobuf
  headers: "authorization=Basic eDIzNDU2NzpnbGNfWFhYWFhYWFhYWA=="
  serviceName: llm-usage-exporter
```

**Step 3: Verify in Grafana Cloud.**

Wait one `OTEL_METRIC_EXPORT_INTERVAL_SECONDS` cycle (default 60s). In Grafana Cloud → **Explore → grafanacloud-<stack>-otlp-metrics** (or use Metrics Drilldown). Search for `llm_usage_total_tokens` — that's the canonical OTLP instrument name.

Traces show up in **Explore → grafanacloud-<stack>-traces** (managed Tempo). Search for service.name = `llm-usage-exporter` and you'll see one `Client`-kind span per provider poll.

#### Which pattern should you pick?

| Use case | Recommended pattern |
|---|---|
| Already running Prometheus in cluster | Pattern A (remote_write) |
| Want traces too (not just metrics) | Pattern B (OTLP) or both |
| Minimum infrastructure | Pattern B — skip local Prometheus entirely |
| You bill by active-series count | Pattern A with `write_relabel_configs` to keep only what you need |

---

## Step 7 — Wire OpenTelemetry (optional)

If your stack already runs an OTel Collector (or a managed OTel backend like Honeycomb, Datadog, New Relic), the exporter can speak OTLP directly to it.

### 7.1 Decide where OTLP goes

Three common destinations:

1. **Your existing OTel Collector** — point at it, the collector fans out to your real backends
2. **A managed OTel backend directly** — Grafana Cloud (Pattern B above), Honeycomb, Datadog OTel intake, etc.
3. **A local Collector for development** — `deploy/otel-collector/` ships a worked config

### 7.2 Wire to an in-cluster OTel Collector

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector.observability.svc.cluster.local:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_SERVICE_NAME=llm-usage-exporter
```

A complete worked Collector config is at [deploy/otel-collector/otel-collector-config.yaml](../deploy/otel-collector/otel-collector-config.yaml) — copy it as a starting point. The README at [deploy/otel-collector/](../deploy/otel-collector/) walks through deploying it locally with Docker and in Kubernetes via the OTel Operator.

### 7.3 What signals you'll see

| Signal | Where it shows up |
|---|---|
| `llm.usage.input_tokens`, `llm.usage.output_tokens`, `llm.usage.total_tokens`, `llm.usage.cached_input_tokens`, `llm.usage.requests`, `llm.usage.cost_usd` | OTel metrics pipeline |
| One `Client`-kind span per `(tenant, provider)` poll | OTel traces pipeline |
| Outbound HTTP child spans (provider API calls) | Same traces pipeline — joined via W3C `traceparent` when `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED=true` |

Tags on every signal: `provider`, `model`, `tenancy_id`, `tenant`.

Outbound provider calls propagate W3C Trace Context by default. Set `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED=false` if your privacy or vendor-governance policy does not allow trace IDs to leave your cluster.

### 7.4 Both planes work together

The exporter doesn't pick *Prometheus or OTLP* — both run side by side from the same internal `LlmUsageBucket` stream. Per-publisher exception isolation means an OTLP backend outage cannot stop the Prometheus surface. Run `/metrics` for SRE alerting and OTLP for the FinOps team's vendor backend.

---

## Step 8 — Configure alerts

The exporter publishes alert primitives as Prometheus gauges (`llm_alerts_budget_burn_ratio`, `llm_alerts_cost_anomaly_score`, etc.) — your Alertmanager rules consume them.

### 8.1 Drop in the shipped rules

[deploy/alerts/llm-usage-exporter.rules.yml](../deploy/alerts/llm-usage-exporter.rules.yml) ships 11 alerts in three groups:

- **Health** — exporter down, unhealthy, poll failures, stale data, slow polls
- **Budgets** — burn at 50% / 80% / 100%
- **Anomalies** — cost / token z-scores at ±2σ / ±3σ

### 8.2 For vanilla Prometheus

```yaml
rule_files:
  - /etc/prometheus/rules/llm-usage-exporter.rules.yml
```

Validate before deploying:

```bash
promtool check rules deploy/alerts/llm-usage-exporter.rules.yml
# Checking deploy/alerts/llm-usage-exporter.rules.yml
#   SUCCESS: 11 rules found
```

### 8.3 For Prometheus Operator

Wrap as a `PrometheusRule` CRD — see [deploy/alerts/README.md](../deploy/alerts/README.md) for the exact YAML.

### 8.4 Severity ladder

The rules use `info` / `warning` / `critical`. Suggested routing:

| Severity | Suggested routing |
|---|---|
| `info` | Dashboard banner / chat-only (50% budget, slow polls) |
| `warning` | Working-hours pager (80% budget, 2σ anomaly, provider failures) |
| `critical` | 24/7 pager (budget exceeded, 3σ anomaly, exporter down) |

### 8.5 Tuning

The shipped thresholds are reasonable starting points. Adjust:

- **Z-score thresholds** — every workload's normal variance is different. If you get too many `LlmCostAnomaly` alerts, widen `ALERTS_ROLLING_WINDOW_BUCKETS` (default 60 = ~5 hours at default poll cadence) or raise the thresholds in the rule file.
- **`for:` durations** — tighten if your poll interval is shorter than 300s; loosen if you want fewer transient alerts.

---

## Step 9 — Set up budgets

Budgets are the most actionable alerting primitive — they translate "spend per period" into a single Prometheus gauge that Alertmanager can route directly.

### 9.1 The configuration shape

Budgets live in the `Alerts:Budgets[]` config section. **Cannot be set via env vars** — they're a structured list, so they need to come from `appsettings.json` or a mounted ConfigMap.

```json
{
  "Alerts": {
    "Budgets": [
      {
        "Name": "monthly-engineering",
        "LimitUsd": 5000,
        "Period": "Monthly"
      },
      {
        "Name": "weekly-ml-team",
        "LimitUsd": 1200,
        "Period": "Weekly",
        "Tenants": ["ml-team"]
      },
      {
        "Name": "daily-gpt-4o",
        "LimitUsd": 100,
        "Period": "Daily",
        "Providers": ["openai"],
        "Models": ["gpt-4o"]
      }
    ]
  }
}
```

Filter fields are optional and AND-ed together. Omitting all of `Providers` / `Models` / `Tenancies` / `Tenants` makes the budget global.

### 9.2 For local Docker Compose

Drop the JSON into [src/LlmUsageExporter.Api/appsettings.Development.json](../src/LlmUsageExporter.Api/appsettings.Development.json) and restart.

### 9.3 For Kubernetes

Mount as a ConfigMap — see [docs/deployment.md → Step 3](deployment.md#step-3--wire-budgets-via-a-configmap) for the canonical example.

### 9.4 What you'll see

After one `ALERTS_EVALUATION_INTERVAL_SECONDS` cycle (default 60s):

```bash
curl -s http://localhost:8080/metrics | grep llm_alerts_budget_
# llm_alerts_budget_limit_usd{budget_name="monthly-engineering"} 5000
# llm_alerts_budget_spend_usd{budget_name="monthly-engineering"} 1342.85
# llm_alerts_budget_burn_ratio{budget_name="monthly-engineering"} 0.26857
# llm_alerts_budget_period_start_timestamp{budget_name="monthly-engineering"} 1730419200
```

Grafana **LLM Usage — Cost & Budgets** dashboard renders these on a bargauge.

---

## Step 10 — Multi-tenant deployment

If you're running the exporter as a shared platform service across many product teams (or many customer organizations), use multi-tenant mode. In single-tenant mode every metric carries `tenant="default"`; in multi-tenant mode each tenant has its own credentials and `tenant="<id>"`.

### 10.1 When you want this

- One LLM bill per business unit / product / customer
- Different teams use different OpenAI orgs / Azure subscriptions / GCP projects
- Need per-tenant chargeback and isolation
- Want `/metrics?tenant=<id>` to filter the exposition for tenant-scoped consumers

### 10.2 The configuration shape

```json
{
  "Tenants": {
    "Items": [
      {
        "Id": "acme",
        "Name": "Acme Corp",
        "OpenAi": {
          "BaseUrl": "https://api.openai.com",
          "AdminApiKey": "sk-admin-acme-..."
        },
        "Anthropic": {
          "BaseUrl": "https://api.anthropic.com",
          "AdminApiKey": "sk-ant-admin-acme-..."
        }
      },
      {
        "Id": "globex",
        "Name": "Globex",
        "OpenAi": {
          "BaseUrl": "https://api.openai.com",
          "AdminApiKey": "sk-admin-globex-..."
        }
      }
    ],
    "ApiKeys": {
      "acme": "bearer-token-for-acme-scrapes",
      "globex": "bearer-token-for-globex-scrapes"
    }
  }
}
```

Each tenant's `Items[N].<Provider>` block is null-safe — Globex above only configures OpenAI, so it'll have one `LlmProviderRegistration` while Acme has two.

### 10.3 Scrape paths

- `GET /metrics` — full exposition (every tenant), no auth
- `GET /metrics?tenant=acme` — Acme only, requires `Authorization: Bearer bearer-token-for-acme-scrapes` if `Tenants:ApiKeys:acme` is set
- `GET /metrics?tenant=globex` — same pattern

Tenant-scoped scrape config example:

```yaml
scrape_configs:
  - job_name: "llm-usage-exporter-acme"
    metrics_path: /metrics
    params:
      tenant: [acme]
    authorization:
      credentials: bearer-token-for-acme-scrapes
    static_configs:
      - targets: ["llm-usage-exporter:8080"]
```

### 10.4 What about budgets in multi-tenant mode?

Budgets have a `Tenants` filter — see Step 9. A budget with `"Tenants": ["acme"]` only includes Acme's spend.

---

## Step 11 — Production deployment (Kubernetes / Helm)

For production deployment, the Helm chart at [deploy/helm/llm-usage-exporter/](../deploy/helm/llm-usage-exporter/) is the canonical path. The chart includes Deployment, Service, ConfigMap, Secret, optional ServiceMonitor, and a separate Secret for the Gemini service-account keyfile.

### 11.1 Verify the image signature first

This is the deployment gate — fail closed on a missing signature:

```bash
cosign verify ghcr.io/xops-labs/llm-usage-exporter:v0.X.Y \
  --certificate-identity-regexp "https://github.com/xops-labs/llm-usage-exporter/.*" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com"
```

Every released image is signed by `cosign` keyless against Sigstore Fulcio, ships with embedded SLSA L3 build provenance, and has CycloneDX + SPDX SBOMs attached to the GitHub Release. See [SECURITY.md → Supply-chain verification](../SECURITY.md#supply-chain-verification) for the full incantation.

### 11.2 Author a production `values.yaml`

A canonical, production-targeted `values-production.yaml` lives at [docs/deployment.md → Step 2](deployment.md#step-2--author-the-production-valuesyaml) — it combines five providers (with External Secrets Operator wiring), file-backed checkpoints on a PVC, OTLP to a Collector, ServiceMonitor, and budget config via ConfigMap.

Minimal example to get started:

```yaml
image:
  repository: ghcr.io/xops-labs/llm-usage-exporter
  tag: v0.X.Y
  pullPolicy: IfNotPresent

openai:
  enabled: true
  adminApiKey: sk-admin-...   # consider mounting via External Secrets / Vault

anthropic:
  enabled: true
  adminApiKey: sk-ant-admin-...

serviceMonitor:
  enabled: true
  labels:
    release: kube-prometheus-stack
```

### 11.3 Install

```bash
helm upgrade --install llm-usage-exporter ./deploy/helm/llm-usage-exporter \
  --namespace observability --create-namespace \
  -f values-production.yaml
```

### 11.4 Production checklist

Before going live, read [docs/deployment.md](deployment.md) in full. The high-impact items:

- [ ] Image signature verified (`cosign verify`)
- [ ] Credentials live in your secrets manager, not in `values.yaml`
- [ ] `/metrics` is not publicly exposed — cluster-internal or behind auth
- [ ] Resource requests / limits sized for your scale (see the table in `docs/deployment.md` Step 6)
- [ ] Single-replica deployment — `FileCheckpointStore` doesn't coordinate across replicas
- [ ] Alertmanager rules from `deploy/alerts/` are loaded
- [ ] Budgets are defined for at least the high-spend products / tenants
- [ ] Liveness / readiness probes are configured (Helm chart defaults are good)
- [ ] You verified `llm_exporter_poll_success_total{provider="<name>"}` is incrementing for each enabled provider

### 11.5 Cloud-specific credential patterns

For zero-secret deployments in cloud Kubernetes, prefer the cloud-native credential injection:

| Cloud | Pattern | Where to read |
|---|---|---|
| AWS EKS | IRSA (IAM Roles for Service Accounts) | [docs/credentials/aws-bedrock.md → Option A](credentials/aws-bedrock.md) |
| GCP GKE | Workload Identity | [docs/credentials/gemini.md → Rotation](credentials/gemini.md) |
| Azure AKS | Workload Identity (AAD) | [docs/credentials/azure-openai.md](credentials/azure-openai.md) |

These avoid storing long-lived secrets in cluster — the cloud injects short-lived tokens via a projected volume.

---

## Step 12 — Day-2 operations

### 12.1 What to watch

The minimum dashboard rotation:

1. **LLM Usage — Exporter Health** — at least once per day. Confirm every provider's freshness is green.
2. **LLM Usage — Cost & Budgets** — when an alert fires. Tells you which budget, which provider, which model.
3. **LLM Usage — Overview** — for spot-checking total spend trends.

The minimum alert routing:

- `LlmUsageExporterDown` (critical) — 24/7 page
- `LlmBudgetExceeded` (critical) — 24/7 page to the budget owner
- `LlmCostAnomalySevere` (critical) — 24/7 page
- Everything else (warning / info) — working-hours pager or chat-only

### 12.2 Upgrades

Standard rolling update via `helm upgrade`. The exporter has no schema migrations:

```bash
# 1. Verify the new image signature
cosign verify ghcr.io/xops-labs/llm-usage-exporter:v0.X.Y \
  --certificate-identity-regexp "https://github.com/xops-labs/llm-usage-exporter/.*" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com"

# 2. Review CHANGELOG.md for breaking changes (none have shipped yet)

# 3. Upgrade
helm upgrade llm-usage-exporter ./deploy/helm/llm-usage-exporter \
  --namespace observability \
  -f values-production.yaml \
  --set image.tag=v0.X.Y

# 4. Verify
kubectl rollout status -n observability deployment/llm-usage-exporter
curl -s http://<svc>/health | jq .
```

The 60-minute lookback window means a brief restart doesn't lose data — the new pod re-reads the same buckets and idempotent publishing prevents double-counting.

### 12.3 Credential rotation

Per-provider runbooks have a **Rotation** section each. General pattern:

1. Stage the new credential in your secrets manager
2. Update the relevant tenant block (or env var)
3. `helm upgrade` (or wait for External Secrets reconcile + pod restart)
4. The exporter validates the new credential at startup via `.ValidateOnStart()` — misconfigured credentials surface as a clear validation error, not as a silent failure mid-poll

### 12.4 When things break

[docs/troubleshooting.md](troubleshooting.md) is organized by symptom — find what you're seeing, follow the diagnostic steps. The top-3 starting commands:

```bash
# 1. Is the exporter alive?
curl -s http://localhost:8080/health | jq .

# 2. Is Prometheus scraping it?
curl -s 'http://localhost:9090/api/v1/targets?state=active' | jq '.data.activeTargets[]
  | select(.scrapePool=="llm-usage-exporter")
  | {health,scrapeUrl,lastError}'

# 3. Are any providers polling successfully?
curl -s http://localhost:8080/metrics | grep -E '^llm_exporter_(poll_success|last_success_timestamp)'
```

---

## Appendix A — Configuration quick reference

All configuration is environment-variable based. The exhaustive reference is in [docs/configuration.md](configuration.md); this is a cheat sheet.

### Exporter-level

| Variable | Default | Purpose |
|---|---|---|
| `EXPORTER_POLL_INTERVAL_SECONDS` | `300` | How often each provider is polled |
| `EXPORTER_LOOKBACK_MINUTES` | `60` | Rolling lookback window per poll (must be > interval) |

### Provider sentinels (set to enable; leave unset to skip)

| Provider | Sentinel | Full runbook |
|---|---|---|
| OpenAI | `OPENAI_ADMIN_API_KEY` | [credentials/openai.md](credentials/openai.md) |
| Azure OpenAI | `AZURE_OPENAI_TENANT_ID` | [credentials/azure-openai.md](credentials/azure-openai.md) |
| Anthropic | `ANTHROPIC_ADMIN_API_KEY` | [credentials/anthropic.md](credentials/anthropic.md) |
| Gemini | `GEMINI_PROJECT_ID` | [credentials/gemini.md](credentials/gemini.md) |
| Bedrock | `AWS_ACCESS_KEY_ID` | [credentials/aws-bedrock.md](credentials/aws-bedrock.md) |

### Cross-cutting features

| Variable | Default | Purpose |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | (unset) | Setting this activates OTLP export |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | (unset) | Comma-separated `k=v` pairs for OTLP auth |
| `CHECKPOINTS_PROVIDER` | `InMemory` | `InMemory` or `File` |
| `CHECKPOINTS_FILE_PATH` | `./data/checkpoints.jsonl` | Only when `File` |
| `ALERTS_ENABLED` | `true` | Toggle alert evaluator |
| `ALERTS_ROLLING_WINDOW_BUCKETS` | `60` | Anomaly z-score baseline window |
| `FOCUS_ENABLED` | `true` | Toggle `/focus.csv` and `/focus.json` |
| `FOCUS_MAX_RECORDS` | `50000` | Bounded in-memory record cap |

### What can only be set via JSON config (not env vars)

- `Tenants:Items[]` — multi-tenant blocks (see Step 10)
- `Tenants:ApiKeys` — bearer tokens for tenant-scoped scrapes
- `Alerts:Budgets[]` — budget definitions (see Step 9)

Mount as `appsettings.Production.json` via a ConfigMap in Kubernetes.

---

## Appendix B — Common questions

### Why is `/metrics` empty after 5 minutes?

Wait one more poll cycle. Real metrics first appear when the first successful poll completes, which can take up to `EXPORTER_POLL_INTERVAL_SECONDS` (default 300s). If you've waited longer, check `llm_exporter_poll_success_total{provider="<name>"}` — if it's zero, polling is failing. See [troubleshooting.md → Polling problems](troubleshooting.md).

### How is this different from the providers' own dashboards?

Three differences:

1. **Near-real-time operational signal** — the exporter polls every 5 minutes; token usage is typically visible within minutes, while cost data still follows each provider's billing-history delay
2. **Unified shape** — five providers in one Prometheus surface, with the same labels and metric naming
3. **Native to your alerting** — Alertmanager rules on `llm_alerts_budget_burn_ratio` page someone exactly the way `up == 0` does

### Does the exporter store my data?

No. It republishes provider data on a polling loop with in-memory bucket dedup. The optional `FileCheckpointStore` persists *bucket identities* (timestamps + tenancy IDs), not the actual usage data, and only for restart-replay safety. Provider remains authoritative.

### Can I run it without Docker?

Yes: `dotnet run --project src/LlmUsageExporter.Api` after `dotnet restore`. You'll need the .NET 10 SDK (pinned by [`global.json`](../global.json)). The Docker path is still recommended because it pins the *runtime* image as well.

### Why does Anthropic show empty for `requests_total`?

Anthropic's organization admin API doesn't expose request counts — only token counts and cost. The exporter doesn't synthesize what the upstream doesn't return. Use `llm_usage_total_tokens_total{provider="anthropic"}` as a proxy for activity. Full availability matrix in [docs/metrics.md](metrics.md).

### How do I add a new provider?

Open an issue first describing which provider, what credential model, and which usage / cost APIs are available. Provider addition is a contained PR — see [CONTRIBUTING.md](../CONTRIBUTING.md) for the pattern (one typed HTTP client + one `ILlmUsageProvider` per provider, plus DI extension and tests; the unified `LlmMetricsPublisher` automatically picks up the new `provider` label value).

### Is `/metrics` safe to expose publicly?

No. Treat it as operational data — put it behind your normal internal network controls, mTLS, or an authenticating reverse proxy. The metric values themselves don't include credentials (verified by source review and `*_logs API keys, client secrets, or bearer tokens` is part of the regression suite), but exposing your org's cost trend publicly is rarely what you want.

### Can multiple replicas share a `FileCheckpointStore`?

No. The file-backed store is local to a single pod's PVC. For horizontal scale, shard tenants across multiple exporter deployments — each pinned to a tenant subset, each with its own checkpoint PVC. See [docs/deployment.md → Step 7](deployment.md#step-7--high-availability-and-the-multi-replica-caveat).

### Where do FOCUS records go?

They're served at `/focus.csv` and `/focus.json`. The exporter does not push them anywhere — your FinOps tool pulls them on its own cadence. Use them to join LLM spend with cloud spend in tools like Cloudability, Vantage, Apptio, OpenCost, and CloudZero. Concrete S3, SFTP, API relay, ConfigMap, and warehouse import patterns live in [docs/standards.md](standards.md#importing-focus-exports-into-downstream-finops-tools).

### How do I disable a feature I don't want?

| Feature | Disable via |
|---|---|
| A provider | Unset its sentinel env var |
| OTLP | Unset `OTEL_EXPORTER_OTLP_ENDPOINT` |
| Alerts (anomaly + budgets) | `ALERTS_ENABLED=false` |
| FOCUS endpoints | `FOCUS_ENABLED=false` |
| File-backed checkpoints | `CHECKPOINTS_PROVIDER=InMemory` (default) |

---

## Appendix C — Contributing back

The exporter is Apache-2.0 and accepts community contributions. The bar to clear:

1. **Tests** — 86 unit tests pass on every PR. Add tests for new behavior. Run them with `dotnet test llm-usage-exporter.slnx`.
2. **Schema-stable metrics** — adding a new label requires an RFC-lite discussion (see [GOVERNANCE.md](../GOVERNANCE.md)). Existing dashboards must keep working.
3. **Commit conventions** — imperative mood, short subject line, descriptive body explaining the "why".

See [CONTRIBUTING.md](../CONTRIBUTING.md) for the full workflow. Good first issues are tagged `good first issue`; help-wanted issues are tagged `help wanted`.

---

## You're done

If you followed every step above, you have:

- The exporter running locally with at least one real provider
- Metrics flowing into your Prometheus (self-hosted or Grafana Cloud)
- Six dashboards rendering live data in Grafana
- 11 alerting rules loaded in Prometheus
- Budgets defined and `llm_alerts_budget_*` gauges populated
- (Optionally) OTLP traces flowing into your OTel backend
- The image verified, the chart deployed, the runbook bookmarked

That's the full surface. The reference docs go deeper on each step — this guide's job was to put them in order.

Open an issue or a discussion if anything in this walkthrough didn't work for you: https://github.com/xops-labs/llm-usage-exporter
