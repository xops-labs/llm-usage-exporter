# Cardinality planning

Every active time-series in Prometheus or an OTLP backend costs memory in the TSDB head
block. For most deployments the exporter's series count is comfortably low, but certain
combinations of many tenants, many providers, and many models can push it into the tens of
thousands. This page gives you the formula, deployment-size estimates, and concrete filtering
recipes so you can plan before you deploy.

## Label dimensions

The four dimensions that drive cardinality are defined in [metrics.md](metrics.md):

| Dimension | Variable | Description |
|---|---|---|
| `tenant` | T | Configured tenants. Single-tenant deployments always have T = 1. |
| `provider` | P | Active providers (up to 5). |
| `model` | M_p | Provider-reported model IDs visible after optional model filters. |
| `tenancy_id` | I_p | Provider-native organization scope per provider: OpenAI/Gemini project, Anthropic workspace, Azure OpenAI account resource, Bedrock region. |
| Budgets | B | Budgets configured in the `Alerts:Budgets` section. |

## Series count formula

```
S_usage   = 6 × T × Σ_p (M_p × I_p)    # 6 model-dimensioned usage families
S_cost    = 1 × T × Σ_p (I_p)           # cost_usd_total — no model label
S_health  = 5 × T × P                   # health families — no model or tenancy_id
S_budgets = 4 × B                        # budget alerts — indexed by budget_name only
S_anomaly = 2 × T × Σ_p (M_p)          # anomaly scores — no tenancy_id

S_total = S_usage + S_cost + S_health + S_budgets + S_anomaly
```

### What counts as a "model"

`M_p` is the number of distinct model IDs the provider API returns **after** any configured
model filter (`OPENAI_MODELS`, `ANTHROPIC_MODELS`, `BEDROCK_MODEL_IDS`, etc.). Without a
filter, all models ever seen in the rolling lookback window are emitted. OpenAI alone
surfaces 100+ model variants; a model filter is strongly recommended for large deployments.

### What counts as a "tenancy_id"

`I_p` is the number of distinct values that appear in the `tenancy_id` label for a given
provider. For OpenAI this is the number of projects; for Anthropic, the number of workspaces;
for Azure OpenAI, the number of account resource IDs configured; for Bedrock, the number of
distinct AWS regions queried.

### The six high-cardinality families

These families carry all four dimensions (tenant, provider, model, tenancy_id) and dominate
the series count at scale:

| Family | Notes |
|---|---|
| `llm_usage_input_tokens_total` | All providers |
| `llm_usage_output_tokens_total` | All providers |
| `llm_usage_total_tokens_total` | All providers |
| `llm_usage_cached_input_tokens_total` | OpenAI and Anthropic only (emitted when cache hits > 0) |
| `llm_usage_requests_total` | All providers except Anthropic |
| `llm_usage_cost_usd_by_model_total` | All providers |

## Deployment size estimates

These examples use the formula above. Column headings: T = tenants, P = active providers,
avg M = average models per provider (after filters), avg I = average tenancy_ids per
provider, B = budgets.

| Deployment | T | Providers | avg M | avg I | B | **Estimated series** |
|---|---|---|---|---|---|---|
| Small — one team, one provider | 1 | 1 | 4 | 1 | 1 | **≈ 34** |
| Small — one team, three providers | 1 | 3 | 4 | 2 | 2 | **≈ 166** |
| Medium — platform team, 3 providers, 5 tenants | 5 | 3 | 5 | 2 | 5 | **≈ 980** |
| Medium — 5 providers, 10 tenants | 10 | 5 | 6 | 3 | 10 | **≈ 12,500** |
| Large — 5 providers, 30 tenants, filtered models | 30 | 5 | 8 | 3 | 20 | **≈ 37,000** |
| XL — 5 providers, 100 tenants, many projects | 100 | 5 | 12 | 6 | 50 | **≈ 365,000** |

**Thresholds to watch:**

| Range | Guidance |
|---|---|
| < 2 000 | No action needed. Default Prometheus / VictoriaMetrics settings are fine. |
| 2 000 – 20 000 | Monitor TSDB head memory. Consider model filters to control growth. |
| 20 000 – 100 000 | Add label-dropping rules or recording rules before deploying to a shared cluster. Set `--storage.tsdb.retention.size`. |
| > 100 000 | Label-dropping is required. Use the Collector attribute filtering or Prometheus relabel rules below. |

### Using the calculator

Run `tools/cardinality-calc` with your actual numbers before enabling new providers or tenants:

```sh
./tools/cardinality-calc \
  --tenants 10 \
  --provider openai:models=8:tenancy_ids=5 \
  --provider anthropic:models=4:tenancy_ids=3 \
  --provider bedrock:models=12:tenancy_ids=2 \
  --budgets 8
```

Interactive mode (no args) will prompt for each value.

## Reducing cardinality

There are three places to cut: at the source (provider filters), in the OTLP Collector
(attribute filtering), and at the Prometheus scrape layer (relabeling). They compose — apply
whichever layers fit your stack.

### 1. Provider model filters (reduce M_p at the source)

The cheapest reduction is upstream: tell the exporter to request only the models you care
about. These filters are applied before any metrics are emitted, so they reduce both the
series count and the number of rows fetched from the provider API.

```sh
# Only emit metrics for these OpenAI models:
OPENAI_MODELS=gpt-4o,gpt-4o-mini,o1-mini

# Only emit metrics for these Anthropic models:
ANTHROPIC_MODELS=claude-3-5-sonnet-20241022,claude-3-haiku-20240307

# Only emit metrics for these Bedrock model IDs:
BEDROCK_MODEL_IDS=anthropic.claude-3-5-sonnet-20241022-v2:0,amazon.titan-text-lite-v1

# Only emit metrics for specific OpenAI projects:
OPENAI_PROJECT_IDS=proj_prod,proj_staging

# Only emit metrics for specific Anthropic workspaces:
ANTHROPIC_WORKSPACE_IDS=wrkspc_prod
```

> **Note** — these filters affect both token usage and cost metrics. A model or project
> that is filtered out will not appear in any metric family, including `llm_usage_cost_usd_total`.

### 2. OTLP Collector attribute filtering

If you route metrics through the OpenTelemetry Collector
(see [deploy/otel-collector/](../deploy/otel-collector/)), you can drop or rewrite attributes
in the pipeline before they reach the backend. The examples below use processors from
`otel/opentelemetry-collector-contrib`.

#### Drop entire metric families

Use the `filter` processor to suppress metric names you do not need in a given backend.
This is useful when you want model-level detail in one pipeline (Grafana Cloud) but only
cost totals in another (a FinOps tool).

```yaml
# otel-collector-config.yaml
processors:
  filter/llm_drop_anomaly:
    metrics:
      metric:
        # Remove anomaly scores — keep them in Prometheus only via /metrics scrape.
        - 'name == "llm.alerts.cost_anomaly_score"'
        - 'name == "llm.alerts.token_anomaly_score"'

  filter/llm_drop_cached_tokens:
    metrics:
      metric:
        # Drop the cached-tokens counter if your backend bills per instruction token only.
        - 'name == "llm.usage.cached_input_tokens"'
```

#### Drop datapoints by attribute value

Drop individual time-series without removing the whole metric. This is the right tool when
you want to suppress dev/staging tenancy data from a production TSDB.

```yaml
processors:
  filter/llm_prod_only:
    metrics:
      datapoint:
        # Drop everything from the non-production tenant.
        - 'attributes["tenant"] == "dev"'
        # Drop dev OpenAI projects from all metrics.
        - 'attributes["tenancy_id"] == "proj_dev" or attributes["tenancy_id"] == "proj_sandbox"'
        # Drop series with an unresolved model label (sanitised missing value).
        - 'attributes["model"] == "unknown"'
```

#### Delete an attribute dimension entirely

If you have a downstream backend that charges per label cardinality, or you simply do not
need `tenancy_id` in a given pipeline, you can delete it from every datapoint:

```yaml
processors:
  attributes/llm_drop_tenancy_id:
    actions:
      - key: tenancy_id
        action: delete
```

> **Trade-off** — deleting `tenancy_id` merges all projects/workspaces into a single series
> per (tenant, provider, model). Cost attribution across multiple OpenAI projects or Anthropic
> workspaces will be lost for this pipeline. Keep `tenancy_id` in at least one pipeline if you
> need per-project showback.

#### Model allowlist via the transform processor

For fine-grained control, OTTL statements in the `transform` processor let you keep only
approved model values and roll everything else into an `"other"` bucket rather than dropping
it entirely. This preserves aggregate counts while capping model-label cardinality.

```yaml
processors:
  transform/llm_model_cap:
    metric_statements:
      - context: datapoint
        statements:
          # Collapse any model not in the approved set to "other".
          - >
            set(attributes["model"], "other") where
            attributes["provider"] == "openai" and not (
              attributes["model"] == "gpt-4o" or
              attributes["model"] == "gpt-4o-mini" or
              attributes["model"] == "o1-mini"
            )
          - >
            set(attributes["model"], "other") where
            attributes["provider"] == "anthropic" and not (
              attributes["model"] == "claude-3-5-sonnet-20241022" or
              attributes["model"] == "claude-3-haiku-20240307"
            )
```

#### Wire filtering processors into the pipeline

Add the processors you want to the metrics pipeline's `processors` list. Order matters:
apply `filter` before `transform`, and `transform` before `batch`.

```yaml
service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors:
        - filter/llm_prod_only          # drop unwanted series first
        - transform/llm_model_cap       # normalise model labels
        - attributes/llm_drop_tenancy_id  # remove dimension if not needed
        - resource
        - batch
      exporters: [prometheusremotewrite, otlphttp/managed]
```

### 3. Prometheus metric_relabel_configs

For Prometheus scrape-based deployments (no OTLP Collector), use `metric_relabel_configs`
on the scrape job to drop or rewrite labels at ingest time. All rules run after the scrape
and before the sample is written to the TSDB.

```yaml
# prometheus.yml
scrape_configs:
  - job_name: llm-usage-exporter
    static_configs:
      - targets: ["exporter:8080"]

    metric_relabel_configs:

      # ── Drop the tenancy_id label from all series ──────────────────────────
      # Reduces M×I cardinality to M per provider.
      # Trade-off: per-project cost attribution is lost in this Prometheus instance.
      - source_labels: []
        target_label: tenancy_id
        action: labeldrop

      # ── Drop "unknown" model series ────────────────────────────────────────
      # Prevents accumulation of series with sanitised empty model IDs.
      - source_labels: [model]
        regex: unknown
        action: drop

      # ── Keep only production tenants ───────────────────────────────────────
      - source_labels: [tenant]
        regex: dev|staging|sandbox
        action: drop

      # ── OpenAI model allowlist ──────────────────────────────────────────────
      # Drop any OpenAI model series that is not in the approved list.
      # Add new models here when you adopt them.
      - source_labels: [provider, model]
        regex: openai;(?!gpt-4o|gpt-4o-mini|o1-mini).*
        action: drop

      # ── Drop anomaly scores from this Prometheus instance ──────────────────
      # Keep anomaly scores only in Grafana Cloud via the OTLP pipeline.
      - source_labels: [__name__]
        regex: llm_alerts_(cost|token)_anomaly_score
        action: drop

      # ── Collapse model to "other" instead of dropping ──────────────────────
      # Preserves aggregate counts while capping model-label cardinality.
      - source_labels: [provider, model]
        regex: anthropic;(?!claude-3-5-sonnet-20241022|claude-3-haiku-20240307).*
        target_label: model
        replacement: other
```

> **Warning** — `labeldrop` on `tenancy_id` is a destructive one-way transform inside
> Prometheus. If you need per-tenancy granularity later you must re-scrape with the label
> present. For reversible suppression, use the OTLP Collector's `attributes` processor and
> route the unlabelled stream to one backend and the full-fidelity stream to another.

## Privacy and governance

The `tenancy_id` label carries provider-native identifiers (OpenAI project IDs, Azure
subscription paths, GCP project names). Whether these constitute sensitive metadata depends
on your organization's classification policy. If they do:

- Use `labeldrop: tenancy_id` in Prometheus or `attributes/delete` in the Collector to
  suppress the label before it reaches a shared TSDB.
- Combine with `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED=false` to also prevent trace IDs
  from crossing vendor API boundaries (see [configuration.md](configuration.md)).

## See also

- [docs/metrics.md](metrics.md) — full metric catalog and label semantics
- [docs/configuration.md](configuration.md) — model filters, tenant configuration, alert budgets
- [deploy/otel-collector/otel-collector-config.yaml](../deploy/otel-collector/otel-collector-config.yaml) — complete Collector config with filtering blocks
- [deploy/prometheus.yml](../deploy/prometheus.yml) — Prometheus scrape config with relabel examples
- `tools/cardinality-calc` — interactive series-count estimator
