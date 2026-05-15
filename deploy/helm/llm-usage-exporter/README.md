# llm-usage-exporter Helm chart

Deploy [`llm-usage-exporter`](https://github.com/xops-labs/llm-usage-exporter) — a near-real-time
Prometheus exporter for LLM usage and cost across OpenAI, Azure OpenAI, Anthropic Claude, Google
Gemini, and AWS Bedrock — into a Kubernetes cluster.

The chart ships a `Deployment`, `Service`, `ConfigMap`, `Secret`, an optional Prometheus-Operator
`ServiceMonitor`, and a dedicated `Secret` for the Gemini service-account keyfile when one is
supplied inline.

In addition to the five providers, the chart exposes top-level value blocks for OpenTelemetry
OTLP export, durable checkpoint storage, budget/anomaly alerts, and FOCUS v1.0 export — each of
which the exporter consumes via environment variables wired in the `ConfigMap` and `Secret`.

## Install

```sh
helm install llm-usage-exporter ./deploy/helm/llm-usage-exporter \
  --namespace observability --create-namespace \
  -f my-values.yaml
```

By default every provider is **disabled** so the chart installs cleanly without credentials.
Enable each provider you care about by setting `<provider>.enabled=true` and supplying the
matching credentials.

The default image is `ghcr.io/xops-labs/llm-usage-exporter:<appVersion>`. Override with
`--set image.repository=...` and `--set image.tag=...`.

## Scraping

Two scraping modes are supported:

- **Prometheus Operator** — set `serviceMonitor.enabled=true`. The chart only renders the
  `ServiceMonitor` if the `monitoring.coreos.com/v1` CRD is installed in the cluster, so it is
  safe to leave on in mixed environments.
- **Static service annotations** — `prometheusScrape.enabled=true` (the default) adds the
  standard `prometheus.io/scrape` annotations to the `Service`, which most static scrape
  configurations will pick up automatically.

## Example: single-provider OpenAI

```yaml
# values-openai.yaml
openai:
  enabled: true
  adminApiKey: sk-admin-xxxxxxxxxxxxxxxxxxxxxxxx
  orgId: org-xxxxxxxxxxxxxxxxxxxxxxxx
  projectIds:
    - proj_aaaaaaaaaaaaaaaaaaaaaaaa
  models:
    - gpt-4o
    - gpt-4o-mini

prometheusScrape:
  enabled: true
```

```sh
helm install llm-usage-exporter ./deploy/helm/llm-usage-exporter \
  --namespace observability --create-namespace \
  -f values-openai.yaml
```

## Example: multi-provider with credentials in values

```yaml
# values-multi.yaml
openai:
  enabled: true
  adminApiKey: sk-admin-...
  projectIds: ["proj_main"]
azureOpenAi:
  enabled: true
  tenantId: 11111111-1111-1111-1111-111111111111
  clientId: 22222222-2222-2222-2222-222222222222
  clientSecret: "S3cret!"
  subscriptionId: 33333333-3333-3333-3333-333333333333
  resourceGroup: rg-aoai
  accountResourceIds:
    - /subscriptions/3333.../resourceGroups/rg-aoai/providers/Microsoft.CognitiveServices/accounts/myaoai
anthropic:
  enabled: true
  adminApiKey: sk-ant-admin-...
  workspaceIds: ["wrkspc_xxxx"]
bedrock:
  enabled: true
  accessKeyId: AKIA...
  secretAccessKey: "..."
  region: us-east-1
  modelIds:
    - anthropic.claude-3-5-sonnet-20241022-v2:0
    - amazon.titan-text-express-v1
```

For production you should usually create the Kubernetes `Secret` out-of-band (e.g. via
[External Secrets Operator](https://external-secrets.io/)) and disable the per-provider
`adminApiKey`/`clientSecret`/`secretAccessKey` fields so the chart only writes non-secret config.

## Example: full Prometheus Operator integration

```yaml
# values-operator.yaml
image:
  pullPolicy: IfNotPresent

openai:
  enabled: true
  adminApiKey: sk-admin-...
  models: ["gpt-4o", "gpt-4o-mini"]

serviceMonitor:
  enabled: true
  interval: 30s
  scrapeTimeout: 10s
  labels:
    release: kube-prometheus-stack   # matches the operator's selector
  metricRelabelings:
    - sourceLabels: [__name__]
      regex: "llm_.*"
      action: keep

prometheusScrape:
  enabled: false   # the ServiceMonitor handles scraping
```

```sh
helm install llm-usage-exporter ./deploy/helm/llm-usage-exporter \
  --namespace monitoring \
  -f values-operator.yaml
```

## Example: OTLP export to an OTel Collector

```yaml
otel:
  enabled: true
  endpoint: http://otel-collector.observability.svc.cluster.local:4317
  protocol: grpc                       # or http/protobuf
  serviceName: llm-usage-exporter
  tracePropagationEnabled: true         # set false to keep trace IDs inside your cluster
  # Sensitive header value goes through the chart Secret, not the ConfigMap.
  headers: "api-key=replace-me"
```

The exporter emits canonical `llm.usage.*` instruments (counters + observable gauges) **and**
traces. Each provider poll is a Client-kind span. Outbound HTTP calls carry W3C `traceparent`
by default; set `otel.tracePropagationEnabled=false` when trace IDs must not cross the
provider boundary.

## Example: durable checkpoints on a PVC

Create a `PersistentVolumeClaim` named `llm-usage-exporter-checkpoints` in the release namespace,
then point `checkpoints.filePath` at the mount path:

```yaml
checkpoints:
  provider: File
  filePath: /data/checkpoints.jsonl

extraVolumes:
  - name: checkpoints
    persistentVolumeClaim:
      claimName: llm-usage-exporter-checkpoints

extraVolumeMounts:
  - name: checkpoints
    mountPath: /data
```

`extraVolumes` and `extraVolumeMounts` are rendered into the Deployment verbatim, so the entries
must be valid Kubernetes `Volume` and `VolumeMount` objects. The exporter container runs as a
non-root user with a read-only root filesystem, so the mounted volume must be writable by uid
`1000` (or whatever you have configured in `podSecurityContext`).

## Example: alerts and FOCUS export

Both are on by default. The most common reasons to tune them:

```yaml
alerts:
  enabled: true
  rollingWindowBuckets: 120        # widen the anomaly window from the 60-bucket default

focus:
  enabled: true
  maxRecords: 100000               # raise the bounded in-memory store cap
```

To configure budgets themselves, mount a values-driven `ConfigMap` with an
`appsettings.Production.json` containing the `Alerts:Budgets` array — env vars cover
the runtime knobs but not the per-budget definitions.

## Example: Gemini with a service-account keyfile

You can either paste the JSON inline (the chart creates a dedicated `Secret` and mounts it
at `/secrets/gemini-sa.json`), or mount your own `Secret`/`projected` volume and set
`gemini.serviceAccountKeyFile` to its path.

```yaml
gemini:
  enabled: true
  projectId: my-gcp-project
  serviceAccountKey: |
    {
      "type": "service_account",
      "project_id": "my-gcp-project",
      "private_key_id": "...",
      "private_key": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----\n",
      "client_email": "exporter@my-gcp-project.iam.gserviceaccount.com",
      "client_id": "...",
      "token_uri": "https://oauth2.googleapis.com/token"
    }
  enableCostQueries: true
  billingProjectId: my-gcp-project
  billingDatasetProject: my-gcp-project
  billingDatasetId: billing_export
  billingTable: gcp_billing_export_v1_XXXXXX_XXXXXX_XXXXXX
```

## Key values reference

| Key                                  | Default                                          | Description                                                                                  |
| ------------------------------------ | ------------------------------------------------ | -------------------------------------------------------------------------------------------- |
| `image.repository`                   | `ghcr.io/xops-labs/llm-usage-exporter`         | Container image.                                                                             |
| `image.tag`                          | `""` (uses `Chart.AppVersion`)                   | Image tag.                                                                                   |
| `replicaCount`                       | `1`                                              | **Must stay at 1 per Deployment.** Two replicas polling the same tenants double-count. See the HA section below for sharding guidance. |
| `resources`                          | `100m / 128Mi` request, `500m / 512Mi` limit     | Pod resources.                                                                               |
| `podSecurityContext` / `securityContext` | non-root, read-only rootfs, drop all caps        | Hardened by default.                                                                         |
| `service.type` / `service.port`      | `ClusterIP` / `8080`                             | Service exposing `/metrics` and `/health`.                                                   |
| `exporter.pollIntervalSeconds`       | `300`                                            | How often the worker fetches usage from each provider API.                                   |
| `exporter.lookbackMinutes`           | `60`                                             | How far back each poll looks.                                                                |
| `openai.enabled`                     | `false`                                          | Enable OpenAI usage + cost scraping.                                                         |
| `azureOpenAi.enabled`                | `false`                                          | Enable Azure OpenAI scraping via Azure Monitor + Cost Management.                            |
| `anthropic.enabled`                  | `false`                                          | Enable Anthropic Admin API scraping.                                                         |
| `gemini.enabled`                     | `false`                                          | Enable Gemini (Cloud Monitoring + BigQuery billing).                                         |
| `bedrock.enabled`                    | `false`                                          | Enable Bedrock (CloudWatch + Cost Explorer).                                                 |
| `otel.enabled` / `otel.endpoint`     | `false` / `""`                                   | Enable OTLP export (metrics + traces).                                                       |
| `otel.tracePropagationEnabled`       | `true`                                           | Propagate W3C `traceparent` / `tracestate` to provider APIs. Set `false` for strict vendor-boundary governance. |
| `checkpoints.provider`               | `InMemory`                                       | Switch to `File` for crash-restart-safe bucket dedup (mount a volume at `checkpoints.filePath`). |
| `alerts.enabled`                     | `true`                                           | Budget burn + cost / token anomaly evaluator.                                                |
| `focus.enabled`                      | `true`                                           | `/focus.csv` and `/focus.json` FOCUS v1.0 endpoints.                                          |
| `serviceMonitor.enabled`             | `false`                                          | Render a `ServiceMonitor` when the Prometheus Operator CRD is installed.                     |
| `prometheusScrape.enabled`           | `true`                                           | Add `prometheus.io/scrape` annotations on the Service for static scrape configs.             |
| `extraVolumes` / `extraVolumeMounts` | `[]` / `[]`                                      | Mount additional volumes (e.g. a PVC for `FileCheckpointStore`, or a budget-config ConfigMap). |

See [`values.yaml`](./values.yaml) for the complete list of tunables.

## High availability and sharding

### Why `replicaCount: 1` is the default

The exporter is a **polling worker**, not a request server. Running two replicas against the same tenant subset causes double-counting: each pod independently re-polls every API bucket and increments Prometheus counters (`*_usage_tokens_total`, `*_usage_cost_usd_total`) for the same data. There is no leader-election or cross-pod coordination today.

`FileCheckpointStore` is local to the pod's PVC — it deduplicates buckets within one pod across restarts, but it cannot be shared between pods. `InMemoryCheckpointStore` is per-process. Either way, two pods see the same buckets as new.

### Correct HA pattern — single replica behind the Service

The Prometheus scrape surface (`/metrics`) is read-only and stateless. For **scrape HA**, run one replica and rely on the Kubernetes `Service` + liveness probe to restart the pod if it crashes. A rolling restart typically completes in 10–30 s; Prometheus staleness handling absorbs scrape failures of that duration without gaps in dashboards.

```yaml
replicaCount: 1   # do not increase
```

### Horizontal scale — shard tenants across Deployments

To spread load across many tenants or to isolate a noisy tenant, deploy **multiple independent exporter Deployments**, each configured with a disjoint `Tenants:Items` subset. Each Deployment gets its own checkpoint PVC and remains a single-writer.

Example: two Helm releases, tenants split between them.

```bash
# Release A — tenants: openai-team, azure-team
helm install llm-exporter-a ./deploy/helm/llm-usage-exporter \
  --namespace observability \
  -f values-shard-a.yaml

# Release B — tenants: anthropic-team, bedrock-team
helm install llm-exporter-b ./deploy/helm/llm-usage-exporter \
  --namespace observability \
  -f values-shard-b.yaml
```

Wire a separate `ServiceMonitor` (or `prometheus.io/scrape` job) for each release so Prometheus scrapes them independently. Add a `shard` label via `relabelings` if you want to tell the two deployments apart in dashboards.

You can also shard **by provider** instead of by tenant — one Deployment polls OpenAI + Azure, another polls Gemini + Bedrock. Either dimension works as long as no two Deployments poll the same `(tenant, provider)` pair.

### Roadmap — external checkpoint store

An external-backed `ICheckpointStore` (Redis or PostgreSQL) would allow two replicas to share a deduplication table and enable true active-active without double-counting. This is on the roadmap but not yet shipped. Until it is, the sharding approach above is the supported scale-out path.

## Validate locally

```sh
helm lint ./deploy/helm/llm-usage-exporter
helm template demo ./deploy/helm/llm-usage-exporter \
  --set openai.enabled=true \
  --set openai.adminApiKey=test \
  --set serviceMonitor.enabled=true
```
