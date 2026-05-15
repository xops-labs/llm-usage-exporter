# Production deployment guide

This guide takes the exporter from a local Docker Compose smoke test (see `README.md`) to a production-targeted Kubernetes deployment. It assumes you already operate a Prometheus + Grafana stack, have basic Helm familiarity, and have a way to deliver secrets into the cluster (External Secrets Operator, sealed-secrets, Vault Agent Injector, or equivalent). Every step below is intended to be executed as written; placeholders are called out explicitly.

## Prerequisites

- Kubernetes 1.27+ (any conformant cluster — EKS, GKE, AKS, OpenShift, self-hosted)
- Helm 3.x
- Prometheus reachable from the cluster, or Prometheus Operator with the `ServiceMonitor` CRD installed
- Provider credentials provisioned per the [per-cloud runbooks](credentials/) — typically managed by External Secrets Operator or sealed-secrets in production
- Secret handling reviewed against [docs/security-secrets.md](security-secrets.md), including rotation, Kubernetes Secret storage, external secret managers, and log-redaction expectations
- `cosign` 2.x (optional but recommended, for image verification before deployment)
- An OpenTelemetry Collector in the cluster if you plan to enable OTLP export (optional)
- A `StorageClass` capable of provisioning `ReadWriteOnce` PVCs if you intend to use file-backed checkpoints

## Step 1 — Verify the image supply chain

Container images are signed with cosign keyless against the Sigstore Fulcio CA. Verification should be a hard deployment gate in any production cluster — fail closed if the signature does not validate.

```bash
cosign verify ghcr.io/xops-labs/llm-usage-exporter:<tag> \
  --certificate-identity-regexp "https://github.com/xops-labs/llm-usage-exporter/.*" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com"
```

A successful verification prints a JSON array containing the signed payload, certificate subject, and Rekor transparency log entry. A failure prints `Error: no matching signatures` or a certificate validation error — in either case, do not deploy the image. Pin a specific immutable tag (or, better, the image digest) in `values-production.yaml` and re-run `cosign verify` on every upgrade.

For air-gapped environments, mirror the signed image and its signature into your internal registry with `cosign copy` and verify against the mirrored reference.

## Step 2 — Author the production values.yaml

The file below is a complete production-targeted values overlay. It enables all five providers, mounts a PVC for file-backed checkpoints, wires OTLP export to an in-cluster collector, exposes the `ServiceMonitor` for Prometheus Operator, and leaves the hardened pod security defaults from the chart untouched. Treat the inline secret references as placeholders — wire each to an `ExternalSecret` or sealed-secret that your platform already manages.

```yaml
# values-production.yaml
image:
  repository: ghcr.io/xops-labs/llm-usage-exporter
  tag: "v1.0.0"          # pin to a verified, signed tag or digest
  pullPolicy: IfNotPresent

replicaCount: 1          # see Step 7 for the multi-replica caveat

serviceAccount:
  create: true
  name: llm-usage-exporter
  annotations:
    # Optional: annotations used by your external secret controller or sidecars.
    # The exporter itself still reads provider credentials from env vars/secrets.
    eks.amazonaws.com/role-arn: arn:aws:iam::123456789012:role/llm-usage-exporter
    # For GKE secret controllers, this may be a Workload Identity binding:
    # iam.gke.io/gcp-service-account: llm-usage-exporter@my-project.iam.gserviceaccount.com

resources:
  requests:
    cpu: 200m
    memory: 256Mi
  limits:
    cpu: 1000m
    memory: 768Mi

# Hardened defaults are inherited from the chart — do not override.
# podSecurityContext: runAsNonRoot=true, runAsUser=1000, fsGroup=1000
# securityContext: readOnlyRootFilesystem=true, allowPrivilegeEscalation=false,
#                  capabilities.drop=[ALL]

env:
  EXPORTER_POLL_INTERVAL_SECONDS: "300"
  EXPORTER_LOOKBACK_MINUTES: "60"
  CHECKPOINTS_PROVIDER: "File"
  CHECKPOINTS_FILE_PATH: "/data/checkpoints.jsonl"
  CHECKPOINTS_MAX_ENTRIES: "100000"
  OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector.observability.svc.cluster.local:4317"
  OTEL_EXPORTER_OTLP_PROTOCOL: "grpc"
  OTEL_SERVICE_NAME: "llm-usage-exporter"
  ASPNETCORE_ENVIRONMENT: "Production"

envFromSecret:
  # ExternalSecret-managed secret containing provider credentials.
  # Keys mirror Tenants:Items[].Credentials:* env-style overrides.
  name: llm-usage-exporter-credentials

# Multi-tenant registrations. Each tenant enumerates the providers it polls.
# Credentials are pulled from envFromSecret; only references appear here.
tenants:
  items:
    - id: default
      label: "Acme Production"
      providers:
        openai:
          enabled: true
        azureOpenAI:
          enabled: true
          endpoint: "https://acme-prod.openai.azure.com"
        anthropic:
          enabled: true
        gemini:
          enabled: true
          projectId: "acme-prod"
        bedrock:
          enabled: true
          regions: ["us-east-1", "us-west-2"]
  apiKeys:
    # Bearer tokens gating /metrics?tenant=<id>; rotate via ExternalSecret.
    default: "${TENANT_API_KEY_DEFAULT}"

checkpoints:
  provider: File
  filePath: /data/checkpoints.jsonl

persistence:
  enabled: true
  storageClass: gp3
  accessMode: ReadWriteOnce
  size: 1Gi

extraVolumes:
  - name: checkpoints
    persistentVolumeClaim:
      claimName: llm-usage-exporter-checkpoints
  - name: budget-config
    configMap:
      name: llm-usage-exporter-budgets

extraVolumeMounts:
  - name: checkpoints
    mountPath: /data
  - name: budget-config
    mountPath: /app/appsettings.Production.json
    subPath: appsettings.Production.json
    readOnly: true

service:
  type: ClusterIP
  port: 8080

serviceMonitor:
  enabled: true
  namespace: observability
  interval: 60s
  scrapeTimeout: 30s
  labels:
    release: kube-prometheus-stack
  relabelings:
    - action: labeldrop
      regex: instance

alerts:
  enabled: true
  # Budget definitions live in the mounted appsettings.Production.json
  # (see Step 3). Env-var configuration cannot express Alerts:Budgets[].
```

A few things to call out about this file:

- `replicaCount: 1` is intentional — see Step 7 before increasing it.
- `serviceAccount.annotations` is for your external secret controller, sidecars, or cloud identity integration. The exporter itself still consumes provider credentials from environment variables or mounted secret files as described in the provider runbooks.
- The `envFromSecret` reference is the integration point with your secrets-management stack — the chart will mount every key in that Secret as an environment variable.
- The chart's hardened `podSecurityContext` and `securityContext` are not overridden; the exporter is built to run with a read-only root filesystem and only writes to `/data`.

## Step 3 — Wire budgets via a ConfigMap

Budget definitions live under `Alerts:Budgets[]` in configuration. Each entry takes `Name`, `LimitUsd`, `Period` (`Monthly` | `Weekly` | `Daily`), plus optional `Providers`, `Models`, `Tenancies`, and `Tenants` filters. The `Tenancies` filter matches against the unified `tenancy_id` label, which carries whichever native identifier the provider uses to scope organization-level usage. Because this is a list of objects, it cannot be expressed via environment variables — it must be delivered as a mounted `appsettings.Production.json`.

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: llm-usage-exporter-budgets
  namespace: observability
data:
  appsettings.Production.json: |
    {
      "Alerts": {
        "Budgets": [
          {
            "Name": "monthly-eng",
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

The ConfigMap is mounted by the `extraVolumes` / `extraVolumeMounts` block already shown in Step 2:

```yaml
extraVolumes:
  - name: budget-config
    configMap:
      name: llm-usage-exporter-budgets

extraVolumeMounts:
  - name: budget-config
    mountPath: /app/appsettings.Production.json
    subPath: appsettings.Production.json
    readOnly: true
```

The exporter picks this up via the standard ASP.NET Core configuration layering: when `ASPNETCORE_ENVIRONMENT=Production`, `appsettings.Production.json` is loaded on top of `appsettings.json` and on top of environment variables. A ConfigMap change triggers a pod restart (because the file is mounted with `subPath`); plan budget rollouts accordingly or use a sidecar reloader if you need hot updates.

## Step 4 — Install the chart

```bash
helm upgrade --install llm-usage-exporter ./deploy/helm/llm-usage-exporter \
  --namespace observability --create-namespace \
  -f values-production.yaml
```

After install, the following objects should be present:

```bash
kubectl get pods,svc,pvc,servicemonitor -n observability -l app.kubernetes.io/name=llm-usage-exporter
```

Expected output (abbreviated):

- One `Pod` in `Running` state with both containers (app + any sidecars) ready
- One `Service` of type `ClusterIP` on port 8080
- One `PersistentVolumeClaim` bound (`llm-usage-exporter-checkpoints`, 1Gi)
- One `ServiceMonitor` (if Prometheus Operator is installed) discovered by your Prometheus instance within one scrape interval

Confirm the exporter is healthy:

```bash
kubectl port-forward -n observability svc/llm-usage-exporter 8080:8080
curl -s http://localhost:8080/health
curl -s http://localhost:8080/metrics | head -50
```

The first `*_exporter_poll_success_total` increment appears after the first polling cycle completes (within `EXPORTER_POLL_INTERVAL_SECONDS` of pod start, default 300s).

## Step 5 — Authenticate /metrics, /focus.csv, /focus.json

Treat the three scrape surfaces as operational data — never expose them to the public internet without authentication. Pick one of the three patterns below.

### Pattern A — Cluster-internal only (recommended)

This is the default in the values file above. The Service is `ClusterIP`, Prometheus scrapes via the `ServiceMonitor`, and there is no Ingress. Operators reach the surfaces through `kubectl port-forward` or via an internal-only bastion. No public exposure, no extra moving parts.

For multi-tenant deployments where individual teams need to scrape their own tenant slice, populate `Tenants:ApiKeys:<tenant-id>` with a bearer token and have each consumer Prometheus pass `Authorization: Bearer <token>` along with `?tenant=<id>` to `/metrics`. Issue one token per consumer and rotate via ExternalSecret.

### Pattern B — Ingress with basic auth (nginx-ingress)

If you need to expose the surfaces to an out-of-cluster Prometheus or a managed observability vendor, front the Service with an authenticated Ingress. Two manifests:

```yaml
apiVersion: v1
kind: Secret
metadata:
  name: llm-usage-exporter-basic-auth
  namespace: observability
type: Opaque
stringData:
  auth: |
    # htpasswd -nbm prom-user 'strong-password-here'
    prom-user:$apr1$XXXXXXXX$YYYYYYYYYYYYYYYYYYYYYY
---
apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: llm-usage-exporter
  namespace: observability
  annotations:
    nginx.ingress.kubernetes.io/auth-type: basic
    nginx.ingress.kubernetes.io/auth-secret: llm-usage-exporter-basic-auth
    nginx.ingress.kubernetes.io/auth-realm: "llm-usage-exporter"
spec:
  ingressClassName: nginx
  rules:
    - host: llm-metrics.internal.example.com
      http:
        paths:
          - path: /
            pathType: Prefix
            backend:
              service:
                name: llm-usage-exporter
                port:
                  number: 8080
  tls:
    - hosts: [llm-metrics.internal.example.com]
      secretName: llm-metrics-tls
```

Issue the TLS certificate with `cert-manager` (`ClusterIssuer` of your choice) or your existing PKI. Rotate the basic-auth credential by regenerating the Secret and re-syncing your consumer Prometheus configuration.

### Pattern C — Service mesh / mTLS (Istio / Linkerd)

If a service mesh is already deployed, leave `service.type: ClusterIP` and rely on mesh authn/authz policies. An Istio example restricting access to the Prometheus service account:

```yaml
apiVersion: security.istio.io/v1
kind: AuthorizationPolicy
metadata:
  name: llm-usage-exporter
  namespace: observability
spec:
  selector:
    matchLabels:
      app.kubernetes.io/name: llm-usage-exporter
  action: ALLOW
  rules:
    - from:
        - source:
            principals:
              - "cluster.local/ns/observability/sa/prometheus"
      to:
        - operation:
            paths: ["/metrics", "/focus.csv", "/focus.json"]
```

For Linkerd, attach a `Server` and `ServerAuthorization` (or `HTTPRoute` + `MeshTLSAuthentication` for newer versions) with equivalent scoping.

## Step 6 — Resource sizing

The exporter is bounded by:

- Number of `(tenant, provider)` registrations — one polling worker iteration per pair
- Number of unique `(tenant, provider, model, tenancy_id)` series — the `tenancy_id` label carries whichever native identifier the provider uses to scope organization-level usage (OpenAI / Gemini project, Anthropic workspace, Azure OpenAI resource, Bedrock region)
- Polling frequency (default 300s)
- OTLP export enabled or not — expect roughly 30% more CPU during the export interval
- Checkpoint store size — `InMemory` is bounded by recent identities; `File` is bounded by `CHECKPOINTS_MAX_ENTRIES`

Rough sizing guide (numbers are starting points — measure in your own environment):

| Scale | Tenants | Providers | Approx series | CPU req/limit | Memory req/limit |
|---|---|---|---|---|---|
| Small (default) | 1 | 1-3 | <500 | 100m / 500m | 128Mi / 512Mi |
| Medium | 10 | 5 | 5k | 200m / 1000m | 256Mi / 768Mi |
| Large | 100 | 5 | 50k | 500m / 2000m | 512Mi / 1.5Gi |
| Extra-large | 1000+ | 5 | 500k+ | 1000m / 4000m | 1Gi / 4Gi (consider sharding) |

At extra-large scale, file-backed checkpoints get expensive (write amplification on every poll, JSON-lines append). Consider running multiple exporter deployments each pinned to a tenant subset, with separate Prometheus scrape jobs targeting each. The checkpoint PVC stays per-deployment and remains single-writer.

## Step 7 — High availability and the multi-replica caveat

A single exporter replica is sufficient for nearly all deployments. Polling history APIs is not latency-sensitive, the exporter is stateless during normal operation, and Prometheus scrapes are cheap and idempotent.

**Multi-replica deployments of the same tenant subset will double-count.** `FileCheckpointStore` is local to one pod's PVC; replicas do not coordinate. There is no shared-state coordination layer today — running two replicas pinned to the same tenant means both will re-poll every bucket within the lookback window, replay metric increments, and inflate `*_usage_cost_usd_total`.

If high availability is required for the metric surface itself (not the polling worker), the correct pattern is:

- Run a single exporter replica
- Put a load balancer (or the chart's default Service) in front for `/metrics` reads
- Accept a short outage during pod restart — Prometheus scrape failures during a 10-30s pod restart are absorbed by the normal staleness window

For horizontal scale, **shard tenants across multiple exporter deployments**, each with its own subset of `Tenants:Items` and its own checkpoint PVC. Use separate Prometheus scrape jobs (one `ServiceMonitor` selector per deployment). This is the supported pattern; do not increase `replicaCount` on a single deployment.

If you need true active-active with a shared checkpoint store, that work is on the roadmap (Redis-backed `ICheckpointStore`) but not shipped. Track the issue tracker before designing around it.

## Step 8 — Upgrade path

Standard rolling-update pattern:

1. Verify the new image's signature (`cosign verify` from Step 1) — fail closed if it does not validate
2. Review the [CHANGELOG.md](../CHANGELOG.md) for breaking metric or config changes (none have shipped yet — metrics and env vars are schema-stable)
3. Update the image tag in `values-production.yaml` and run `helm upgrade` — the chart performs a rolling update of the Deployment
4. The new pod completes its first poll before the old pod terminates; the lookback window (`EXPORTER_LOOKBACK_MINUTES`, default 60min) absorbs any gap between the two replicas
5. Verify `*_exporter_poll_success_total` increments after upgrade and `*_exporter_last_success_timestamp` advances for each provider

Downgrade is the same procedure in reverse. The exporter has no schema migrations; the file-format of `checkpoints.jsonl` has been stable since v1.0.0 and any future change will ship behind a versioned envelope.

## Step 9 — Tenant credential rotation

Procedure for rotating a single provider credential within an existing tenant:

1. Stage the new credential in your secrets manager (ESO / sealed-secrets / Vault Agent Injector / SOPS)
2. Update the relevant entry in `Tenants:Items` if the credential reference changes (typically a ConfigMap or values.yaml edit driven by a CI pipeline)
3. Run `helm upgrade` (or wait for ESO reconciliation followed by a pod restart, depending on whether your Secret operator triggers restarts automatically)
4. The exporter validates the new credential at startup via `.ValidateOnStart()` — misconfigured credentials surface as a clear validation error on container start, not as a silent failure at first poll

For zero-downtime rotation in a single tenant — where the old credential will be revoked before the new one is fully propagated — the cleanest path is two adjacent deployments. Run one Deployment pinned to the old credential, one to the new, and use a controlled cutover at the load-balancer or service-mesh routing layer. Once the new deployment has produced its first successful poll, scale the old one to zero.

For revoke-immediately scenarios (compromised credential), accept the short outage: revoke at the provider, update the Secret, and let the rolling update complete. The lookback window will catch up on the missed buckets after the new credential is live.

## Step 10 — Observability of the exporter itself

The exporter is itself an observability target. In production:

- The Helm chart already wires `/health` to the liveness probe. Do not disable it.
- Wire the shipped Alertmanager rules at [deploy/alerts/llm-usage-exporter.rules.yml](../deploy/alerts/llm-usage-exporter.rules.yml) into your Prometheus rules. They cover poll failure rate, last-success staleness, and budget burn signals.
- Watch `*_exporter_poll_failure_total` and `*_exporter_last_success_timestamp` per provider — these are the primary SLO signals. Page when the last-success timestamp is older than `3 * EXPORTER_POLL_INTERVAL_SECONDS`.
- If OTLP is enabled, the polling spans flow to your OTel Collector and become traces — useful for debugging "which provider is slow today" without redeploying for verbose logs.
- Anthropic does not emit `*_usage_requests_total`; alerts based on request volume should exclude that provider. Cached-input alerts (`*_usage_cached_input_tokens_total`) only apply to OpenAI and Anthropic.

## Quick reference

| Concern | Where it's solved |
|---|---|
| Provider credentials | [docs/credentials/](credentials/) per cloud |
| Secret handling and redaction | [docs/security-secrets.md](security-secrets.md) |
| Metric authentication | Step 5 above |
| Multi-tenant credential isolation | README -> Configuration -> Multi-tenant |
| Budget definitions | Step 3 above |
| Checkpoint durability | `CHECKPOINTS_PROVIDER=File` + PVC, Step 2 |
| OTLP wiring | `OTEL_EXPORTER_OTLP_ENDPOINT` env var |
| Alerting | [deploy/alerts/llm-usage-exporter.rules.yml](../deploy/alerts/llm-usage-exporter.rules.yml) |
| Troubleshooting | [docs/troubleshooting.md](troubleshooting.md) |
| Supply-chain verification | Step 1 above + [SECURITY.md](../SECURITY.md) |
| HA limitations and multi-replica caveat | Step 7 above |
| Sharding for horizontal scale | Step 7 above |
| Credential rotation | Step 9 above |
