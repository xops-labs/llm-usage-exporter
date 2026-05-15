# OpenTelemetry Collector example

A worked OpenTelemetry Collector configuration for receiving the OTLP signals emitted by the `llm-usage-exporter` and forwarding them to common downstream backends.

The exporter emits OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Metrics flow under the meter name `LlmUsageExporter` (`llm.usage.input_tokens`, `llm.usage.output_tokens`, `llm.usage.total_tokens`, `llm.usage.cached_input_tokens`, `llm.usage.requests`, `llm.usage.cost_usd`). Traces flow from the `LlmUsageExporter` ActivitySource (one `Client`-kind span per `(tenant, provider)` poll iteration) plus child HTTP spans.

## What [`otel-collector-config.yaml`](otel-collector-config.yaml) does

| Pipeline | Receiver | Processors | Exporters |
|---|---|---|---|
| `metrics` | OTLP gRPC (4317) + HTTP (4318) | `resource` (deployment labels) + `batch` | `debug` + `prometheusremotewrite` + `otlphttp/managed` |
| `traces` | OTLP gRPC + HTTP | `resource` + `tail_sampling` + `batch` | `debug` + `otlp/jaeger` + `otlphttp/managed` |

Pick the exporters that match your stack and remove the others from the `service.pipelines` block.

## Wiring the exporter to this collector

Set the exporter's env vars:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc          # or http/protobuf for port 4318
OTEL_SERVICE_NAME=llm-usage-exporter
# OTEL_EXPORTER_OTLP_HEADERS=api-key=...  # only for managed backends that auth at receive
```

In the Helm chart, the same wiring lives under `otel:` in `values.yaml`:

```yaml
otel:
  enabled: true
  endpoint: http://otel-collector.observability.svc.cluster.local:4317
  protocol: grpc
  serviceName: llm-usage-exporter
```

## Running the collector locally

Pull the OTel Collector contrib distribution (the `tail_sampling` and `prometheusremotewrite` exporters live there, not in the core distro):

```bash
docker run --rm \
  --name otel-collector \
  --network llm-usage-exporter_default \
  -p 4317:4317 -p 4318:4318 -p 8888:8888 \
  -v "$PWD/deploy/otel-collector/otel-collector-config.yaml:/etc/otel/config.yaml:ro" \
  otel/opentelemetry-collector-contrib:0.119.0 \
  --config /etc/otel/config.yaml
```

The collector's own `/metrics` for self-monitoring is at `http://localhost:8888/metrics` — scrape it from your Prometheus.

## Running the collector in Kubernetes

Either deploy the [OpenTelemetry Operator's `OpenTelemetryCollector` CRD](https://github.com/open-telemetry/opentelemetry-operator) (most common), or use the upstream `opentelemetry-collector` Helm chart. Mount this file as a ConfigMap.

## Verifying the pipeline

1. Start the exporter with `OTEL_EXPORTER_OTLP_ENDPOINT` pointing at the collector
2. Tail the collector logs — with the `debug` exporter active you'll see batches of `llm.usage.*` metrics and `poll <provider>` spans printed every poll cycle
3. Confirm the downstream backend received them (check Grafana Cloud / Honeycomb / Jaeger UI)

## Choosing a backend

| Backend | Best for | Add this exporter |
|---|---|---|
| Grafana Mimir / VictoriaMetrics / AMP | A Prometheus-compatible TSDB you already operate | `prometheusremotewrite` |
| Grafana Cloud (Metrics + Traces) | A managed Grafana stack | `otlphttp/managed` |
| Honeycomb / Datadog / New Relic | A managed APM/observability vendor | `otlphttp/managed` |
| Jaeger / Tempo | Traces only | `otlp/jaeger` or `otlp/tempo` |
| AWS X-Ray | AWS-native trace target | use the AWS Distro for OpenTelemetry (ADOT) build with `awsxray` exporter |
| Azure Monitor | Azure-native target | use the `azuremonitor` exporter |

## Metric temporality

The exporter emits all `llm.usage.*` instruments as **cumulative counters** — values accumulate from startup and are never reset between export intervals. This is the correct default for Prometheus-compatible TSDB backends.

### Which backends prefer which temporality

| Backend | Preferred temporality | Action needed |
|---|---|---|
| Grafana Mimir / VictoriaMetrics / AMP | Cumulative | None — default works. |
| Grafana Cloud (Prometheus remote write) | Cumulative | None — default works. |
| Prometheus (pull, via `/metrics`) | Cumulative | None — Prometheus plane is independent of OTLP. |
| Datadog (OTLP intake) | Delta | See option A or B below. |
| New Relic (OTLP intake) | Delta | See option A or B below. |
| Honeycomb | Cumulative | None — default works. |
| AWS X-Ray / CloudWatch EMF | Delta | See option A or B below. |

### Option A — convert at the collector (recommended when mixing backends)

Enable the `cumulativetodelta` processor in `otel-collector-config.yaml` (the block is already present, commented out) and add it to the metrics pipeline before `batch`:

```yaml
service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [resource, cumulativetodelta, batch]
      exporters: [otlphttp/managed]   # your delta-preferring backend
```

This leaves the exporter itself untouched and keeps the Prometheus remote write pipeline on cumulative. The `cumulativetodelta` processor is in the **contrib** distribution — run `otel/opentelemetry-collector-contrib`, not the core build.

### Option B — change temporality at the exporter

Set `OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=Delta` on the exporter. The .NET OTel SDK then emits delta natively for all counter instruments.

Use this only when **all** downstream OTLP consumers expect delta. If you're also feeding a Prometheus remote write or Grafana Mimir backend from the same OTLP stream, use Option A (convert at the collector) to avoid breaking that pipeline.

### Verifying the emitted temporality

With the `debug` exporter active, each metric batch log line shows `temporality: CUMULATIVE` or `temporality: DELTA`. Use that to confirm the pipeline is converting as expected before pointing a production backend at the collector.

---

## Related docs

- [docs/deployment.md](../../docs/deployment.md) — full production deployment guide
- [SECURITY.md](../../SECURITY.md) — `OTEL_EXPORTER_OTLP_HEADERS` may contain API keys; treat them as secrets
- [Upstream OTel Collector contrib repo](https://github.com/open-telemetry/opentelemetry-collector-contrib)
