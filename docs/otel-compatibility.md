# OpenTelemetry compatibility

## The two instrument namespaces: `llm.usage.*` vs. OTel GenAI semconv

`llm-usage-exporter` emits its OTLP instruments under the `llm.usage.*` and `llm.exporter.*` names. These are **not** the same as the OTel GenAI semantic conventions (`gen_ai.*`). Understanding why they differ, and how to map between them, matters when you combine this exporter with an LLM SDK that already emits GenAI semconv.

### Why the names differ

The OTel GenAI semantic conventions ([`gen_ai.*`](https://opentelemetry.io/docs/specs/semconv/gen-ai/)) were designed for **in-process SDK instrumentation** — your application code calls an LLM API, the SDK instruments each call, and span events carry per-request token counts in real time. Attributes like `gen_ai.system`, `gen_ai.request.model`, `gen_ai.token.type`, and metrics like `gen_ai.client.token.usage` live in that per-call, request-scoped world.

`llm-usage-exporter` operates in a different plane: it reads **provider billing-history APIs** — CloudWatch, Azure Monitor, OpenAI Costs API, Anthropic Cost Report, BigQuery billing export — and re-emits aggregated usage and spend as time-series. These APIs return pre-bucketed hourly or daily aggregates, not individual requests. There is no span to attach token counts to; the data is already rolled up by the time the exporter sees it.

The practical consequences of this difference:

| Concern | OTel GenAI (`gen_ai.*`) | `llm-usage-exporter` (`llm.usage.*`) |
|---|---|---|
| Data source | In-process SDK instrumentation | Provider billing-history APIs |
| Granularity | Per API call | Pre-bucketed aggregates (hourly / daily) |
| Latency | Real-time | Provider reporting delay (minutes → days) |
| Cost figures | None (usage only) | Yes — USD cost from billing APIs |
| Deployment model | Added to your application code | Sidecar / standalone exporter |
| Who produces it | Your LLM SDK (LiteLLM, LangChain, etc.) | This exporter |

Using both together is valid and recommended for complete coverage: GenAI semconv gives you per-call latency, error rate, and real-time token throughput from your application layer; `llm.usage.*` gives you cross-provider cost, billing-derived usage aggregates, and budget signals from the provider layer.

---

## Instrument mapping table

The table below maps each `llm.usage.*` instrument to the nearest OTel GenAI semconv equivalent and explains the semantic gap between them.

| `llm.usage.*` instrument | OTel GenAI semconv equivalent | Semantic gap |
|---|---|---|
| `llm.usage.input_tokens` | `gen_ai.client.token.usage{gen_ai.token.type="input"}` | GenAI: per-call, real-time. `llm.usage`: hourly aggregate from billing API. |
| `llm.usage.output_tokens` | `gen_ai.client.token.usage{gen_ai.token.type="output"}` | Same as above. |
| `llm.usage.total_tokens` | Sum of input + output in `gen_ai.client.token.usage` | GenAI does not have a total-tokens metric; this is a convenience counter. |
| `llm.usage.cached_input_tokens` | No direct equivalent | Prompt-cache hits from OpenAI and Anthropic billing APIs; GenAI semconv does not define a cache-hit metric. |
| `llm.usage.requests` | Count of spans with `gen_ai.operation.name="chat"` | GenAI: derived from span count. `llm.usage`: explicit request count from billing API (absent for Anthropic). |
| `llm.usage.cost_usd` | No equivalent | GenAI semconv has no cost metric. This is unique to the billing-plane approach. |
| `llm.usage.cost_usd_by_model` | No equivalent | Same; adds model dimension to cost. |
| `llm.exporter.poll_success` | No equivalent | Exporter self-health; GenAI semconv has no exporter health concept. |
| `llm.exporter.poll_failure` | No equivalent | Same. |
| `llm.exporter.last_success_timestamp` | No equivalent | Observable gauge; no GenAI equivalent. |

---

## Attribute mapping table

The table below maps each attribute on `llm.usage.*` instruments to the OTel GenAI attribute it most closely corresponds to.

| `llm.usage.*` attribute | OTel GenAI attribute | Notes |
|---|---|---|
| `provider` | `gen_ai.system` | Values differ: GenAI uses `openai`, `anthropic`, `vertex_ai`, `aws_bedrock`; `llm.usage.*` uses `openai`, `azure_openai`, `anthropic`, `gemini`, `bedrock`. |
| `model` | `gen_ai.request.model` or `gen_ai.response.model` | Both carry the model identifier. GenAI may have both request and response; `llm.usage.*` uses whatever the billing API returns (typically the resolved model). |
| `tenant` | No equivalent | Multi-tenant grouping key specific to this exporter. |
| `tenancy_id` | No equivalent | Provider-native organization scope (OpenAI project, Anthropic workspace, Azure resource, Bedrock region). No GenAI equivalent. |

---

## Why `llm.usage.*` rather than `gen_ai.*` for the OTLP output?

Adopting `gen_ai.*` names for billing-plane aggregates would create a semantic collision: a consumer seeing `gen_ai.client.token.usage` from this exporter alongside `gen_ai.client.token.usage` from an SDK-instrumented application could not distinguish per-call real-time data from delayed billing aggregates. Different temporality, different freshness, different count semantics — same metric name.

The `llm.usage.*` namespace avoids this collision and makes the data origin explicit. Consumers can join or compare the two namespaces in their dashboards without ambiguity.

---

## Planned alignment with OTel GenAI

`OTEL_GENAI_ATTRIBUTE_ALIGNMENT_MODE=true` is the proposed flag for a future optional mode. It is **not shipped yet**; today the exporter emits canonical `llm.usage.*` OTLP instruments only. When implemented, it would:

- Rename `provider` → `gen_ai.system` with value normalization.
- Rename `model` → `gen_ai.request.model`.
- Rename `llm.usage.input_tokens` → `gen_ai.client.token.usage` with `gen_ai.token.type="input"` (and similar for output).
- Drop `llm.usage.total_tokens` (no GenAI equivalent; consumers sum input + output).

This mode will be opt-in because the attribute rename is a breaking change for any backend that already indexes by `provider` or `model`. It will not be the default. Track [GitHub Issues](https://github.com/xops-labs/llm-usage-exporter/issues) for the milestone.

## Upstream OTel engagement status

No OTel GenAI SIG issue or discussion has been opened yet. The planned sequence is: document the aggregate-history data model clearly, gather post-launch user examples, then raise the upstream question with evidence instead of treating it as a naming dispute. The discussion should ask whether aggregate provider billing/history telemetry belongs in a future `gen_ai.aggregate.*` or `gen_ai.billing.*` convention family while preserving `llm.usage.*` for this exporter's existing users.

---

## OTLP metric temporality

The exporter defaults to **cumulative** temporality — every export batch carries the running total of each counter since process startup, not the delta since the previous export. This matches Prometheus semantics and is correct for Prometheus-compatible backends (Grafana Mimir, VictoriaMetrics, AMP, Grafana Cloud).

Delta-preferring backends (Datadog, New Relic) need either:

- The `cumulativetodelta` processor in the OTel Collector pipeline (see [deploy/otel-collector/otel-collector-config.yaml](../deploy/otel-collector/otel-collector-config.yaml)).
- `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=Delta` on the exporter — only use this if you do **not** also feed a Prometheus-compatible backend from the same OTLP stream.

See [docs/troubleshooting.md → OTLP / tracing problems](troubleshooting.md#otlp--tracing-problems) for the full saw-tooth fix.

---

## See also

- [docs/metrics.md](metrics.md) — full Prometheus metric catalog and label semantics
- [docs/standards.md](standards.md) — standards compliance table including OTel OTLP status
- [deploy/otel-collector/](../deploy/otel-collector/) — OTel Collector config examples
- [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/)
- [OpenTelemetry GenAI metrics](https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/)
