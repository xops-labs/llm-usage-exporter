# Failure-mode matrix

This document is the canonical reference for how llm-usage-exporter behaves under adverse conditions. Platform teams should read it alongside the [alert rules](../deploy/alerts/llm-usage-exporter.rules.yml) and the [Exporter Health dashboard](../dashboards/llm-exporter-health.json).

## Isolation guarantees

The exporter enforces two planes of isolation. Failures in one plane do not propagate to the other.

**Per-provider isolation** — each provider holds its own publisher instance. The `CompositeMetricsPublisher` wraps all calls in a try/catch and logs the error before continuing to the next publisher. A credential failure on Anthropic does not interrupt OpenAI polling.

**Per-output-plane isolation** — Prometheus exposition and OTLP export are separate publishers in the same composite. An OTLP write failure does not interrupt Prometheus metric accumulation, and a Prometheus scrape failure does not interrupt OTLP export.

---

## Matrix

| Scenario | Immediate effect | Metric signal | Alert | Recovery |
|---|---|---|---|---|
| [Provider 429](#provider-429--rate-limiting) | Poll aborts; failure counter increments | `llm_exporter_poll_failure_total{provider}` ↑ | `LlmUsageExporterPollFailures` (warning) after >3 failures in 15 m | Automatic — next poll at configured interval |
| [Provider 5xx](#provider-5xx) | Same as 429 | Same | Same | Automatic |
| [Bad credentials](#bad-credentials) | Repeated poll failures; no data ingested for that provider | `llm_exporter_poll_failure_total` ↑; `llm_exporter_last_success_timestamp` goes stale | `LlmUsageExporterPollFailures` (warning), then `LlmUsageExporterStale` (warning) after 30 m | Rotate credentials; restart pod or trigger config reload |
| [Repeated cursor](#repeated-cursor) | Checkpoint deduplication suppresses the replay; no counter movement | No change in metric counters; checkpoint store absorbs the identity | None | Automatic — no action needed |
| [OTLP endpoint down](#otlp-endpoint-down) | OTLP write fails; error logged; Prometheus exposition continues normally | `otelcol_exporter_send_failed_metric_points_total` ↑ (OTel collector self-metrics) | None from the exporter — configure a collector-side alert on `otelcol_exporter_send_failed_metric_points_total` | Automatic — OTel collector retries with backoff once the endpoint recovers |
| [Prometheus scrape failure](#prometheus-scrape-failure) | Missing scrape appears as a gap in Prometheus TSDB; metric values are held in the registry | `up{job="llm-usage-exporter"} == 0` | `LlmUsageExporterDown` (critical) after 5 m | Automatic on next successful scrape; no exporter restart required |
| [Checkpoint write failure](#checkpoint-write-failure) | `TryRecord` throws; `PublishUsage` propagates; `CompositeMetricsPublisher` catches and logs; that publisher's metrics are skipped for the iteration | Error logged at ERROR level; `llm_exporter_poll_failure_total` does **not** increment because polling succeeded | `LlmUsageExporterStale` may fire if the affected publisher stalls long enough; no `LlmUsageExporterPollFailures` alert | Fix the underlying I/O issue (disk full, permission denied); restart the exporter |
| [Process restart](#process-restart) | File checkpoint store reloads seen identities from disk on startup; previously published buckets are skipped on first poll | Prometheus counters start fresh in the new process; checkpoint prevents double-counting | None (Kubernetes / systemd manages lifecycle) | Automatic |

---

## Per-scenario details

### Provider 429 — rate limiting

Provider APIs enforce rate limits on usage and cost history endpoints. When the exporter receives HTTP 429, the current poll aborts.

**What does NOT happen:**
- Other providers are not affected.
- The checkpoint store is not corrupted — any bucket written in earlier pages of the same poll remains recorded.
- Prometheus counters do not roll back.

**Detection:**
```promql
increase(llm_exporter_poll_failure_total{provider="openai"}[15m]) > 3
```

**Recovery:** The next poll fires at the configured `POLL_INTERVAL_SECONDS`. Most provider APIs include a `Retry-After` header; the exporter logs it but does not currently honor it for adaptive backoff — consider widening the poll interval if 429s are chronic.

**Dashboard:** "Poll failure rate per provider" panel in Exporter Health.

---

### Provider 5xx

HTTP 5xx responses from the provider API are treated identically to 429 — the poll aborts and `llm_exporter_poll_failure_total` increments.

**Detection:** Same as 429. Watch the provider's own status page alongside `llm_exporter_poll_failure_total`.

**Recovery:** Automatic. Most provider 5xx conditions resolve within minutes.

---

### Bad credentials

An expired or revoked API key causes every poll for that provider to fail with HTTP 401 or 403. Unlike 429, these failures do not resolve automatically.

**What does NOT happen:**
- Other providers with valid credentials continue polling.
- OTLP and Prometheus exposition for other providers continue normally.

**Progression:**
1. `llm_exporter_poll_failure_total` increments on each poll cycle.
2. After 3+ failures in 15 m → `LlmUsageExporterPollFailures` fires (warning).
3. After 30 m with no success → `LlmUsageExporterStale` fires (warning).
4. Budget and anomaly alerts for this provider go flat — they stop updating, not zeroing.

**Detection:**
```promql
# Combined staleness check
time() - max by (tenant, provider) (llm_exporter_last_success_timestamp) > 1800
```

**Recovery:**
1. Rotate the credential in the secret store.
2. Update the exporter config (env var / Kubernetes secret).
3. Restart the pod or trigger a reload.
4. Verify freshness recovers: `llm_exporter_last_success_timestamp` should advance within one poll interval.

**See also:** [docs/security-secrets.md](security-secrets.md), [docs/credentials/](credentials/).

---

### Repeated cursor

A "repeated cursor" occurs when the provider emits the same time-window bucket across multiple consecutive poll windows. This is expected behavior — provider billing APIs are not strictly append-only and can re-emit completed hours.

**Mechanism:** `LlmMetricsPublisher.PublishCounterOnce` computes a deterministic identity from `(tenant, provider, startTime, endTime, model, tenancyId, metricType)` and calls `ICheckpointStore.TryRecord`. If the identity was already seen, the counter is not incremented.

**What does NOT happen:**
- Double-counting does not occur.
- The checkpoint store does not consume unbounded memory — entries expire after `CheckpointStoreOptions.RetentionHours` (default 168 h).

**Detection:** No alert fires for this. If you suspect replay is being silently absorbed when it should not be, compare `llm_usage_input_tokens_total` with the provider's own dashboard for the same period.

**Recovery:** None required. This is correct behavior.

---

### OTLP endpoint down

When the OTLP endpoint (typically an OTel collector) is unreachable:

1. The `LlmMeterPublisher` (OTLP side of the composite) will fail to export metric points.
2. `CompositeMetricsPublisher` catches the exception and logs it.
3. The `LlmMetricsPublisher` (Prometheus side) continues normally — Prometheus exposition is unaffected.
4. The OTel SDK's internal exporter queue absorbs the backlog up to `queue_capacity`; once full it drops metric points.

**Detection (OTel collector self-metrics — scraped separately):**
```promql
rate(otelcol_exporter_send_failed_metric_points_total{exporter="otlphttp/managed"}[5m]) > 0
```

Add a Prometheus alert on this metric in your OTel collector's scrape job. The exporter itself does not fire an alert for OTLP failures.

**Recovery:** The OTel SDK retries automatically with exponential backoff. Once the endpoint recovers, the queue drains. Metric points dropped during the outage are permanently lost from the OTLP stream — Prometheus exposition is the reliable fallback surface during an OTLP outage.

**Dashboard:** "OTLP exporter errors" panel in Exporter Health.

---

### Prometheus scrape failure

A scrape failure means Prometheus could not reach the exporter's `/metrics` endpoint. This is distinct from a poll failure — the exporter may be healthy internally.

**Causes:**
- Pod not running / OOMKilled.
- Network policy blocking Prometheus → exporter port 8080.
- `/metrics` returning 5xx (e.g., ASP.NET middleware panic).

**What does NOT happen:**
- Metric values accumulated in the prometheus-net registry are not lost — they accumulate until the next successful scrape.
- OTLP export continues normally.

**Detection:** `up{job="llm-usage-exporter"} == 0` for 5 m → `LlmUsageExporterDown` (critical).

**Recovery:**
1. Check pod status: `kubectl get pods -n observability -l app.kubernetes.io/name=llm-usage-exporter`.
2. Check logs: `kubectl logs -n observability deploy/llm-usage-exporter --previous`.
3. Verify network policy: confirm port 8080 is reachable from the Prometheus pod.
4. The gap in TSDB is permanent once Prometheus misses the scrape window.

---

### Checkpoint write failure

If the file-backed checkpoint store cannot write (disk full, permission error), `TryRecord` throws an exception.

**Call path:**
```
LlmMetricsPublisher.PublishUsage
  → PublishCounterOnce
    → ICheckpointStore.TryRecord   ← throws
      ← exception propagates
CompositeMetricsPublisher.PublishUsage
  → catches, logs ERROR, continues to next publisher
```

**Consequence:** The publisher that uses the failing checkpoint store emits no metrics for the affected poll iteration. If the Prometheus publisher is the one failing, its counters stall. If the OTLP publisher is failing, OTLP stalls but Prometheus continues.

**Detection:**
- ERROR-level log line: `"Inner publisher {Publisher} threw during PublishUsage"`.
- `llm_exporter_poll_failure_total` does not increment (this is a publish-plane failure, not a poll-plane failure).
- Watch for freshness creeping upward: `time() - llm_exporter_last_success_timestamp`.

**Recovery:**
1. Free disk space or fix the permission issue.
2. Restart the exporter — the file checkpoint store reloads on startup.
3. The first poll after restart will re-publish any buckets whose checkpoint was lost, because the in-flight writes that failed were not persisted. **This may advance counters for windows that were previously processed.** The magnitude is bounded to at most one poll window.

**See also:** [docs/deployment.md](deployment.md) for recommended checkpoint volume sizing.

---

### Process restart

Kubernetes pod evictions, rolling deployments, and OOMKills all result in a process restart.

**Mechanism:**
- The file checkpoint store persists seen identities to a JSONL file (`CHECKPOINT_FILE_PATH`).
- On startup, `FileCheckpointStore` reads the file and loads all non-expired identities into memory.
- The first poll after restart deduplicates against the loaded checkpoint — buckets seen before the restart are skipped.

**What does NOT happen:**
- Prometheus counters do not reset across restarts in the TSDB — Prometheus accumulates `increase()` across the counter reset boundary using its reset-detection heuristic.
- Budget and anomaly states are re-evaluated from scratch on restart — a single evaluation cycle after restart is normal.

**Detection:** A counter reset appears in Prometheus as a sudden drop to a low value followed by resumption. `increase(llm_usage_input_tokens_total[5m])` handles this correctly.

**Recovery:** Automatic. Ensure `CHECKPOINT_FILE_PATH` is on a persistent volume (not the container's ephemeral filesystem) so the checkpoint survives pod replacement.

---

## Reading the Exporter Health dashboard

| Panel | What to look for |
|---|---|
| Freshness stats (top row) | All green (≤ 10 m) under normal operation; yellow/red indicates credential or API outage |
| Poll success / failure rate | Failure rate spikes without matching success rate → provider-side issue |
| Poll failure ratio (15 m) | > 20% yellow, > 50% red — sustained failure warrants investigation |
| Per-provider health summary table | Quick snapshot of total successes, failures, staleness, and last duration |
| OTLP exporter errors | Non-zero rate → OTel collector pipeline issue; check collector logs |
| OTLP queue utilization | Approaching 100% → collector is saturated or endpoint is down |

## Alert → runbook mapping

| Alert | Severity | Link |
|---|---|---|
| `LlmUsageExporterDown` | critical | [Prometheus scrape failure](#prometheus-scrape-failure) |
| `LlmUsageExporterUnhealthy` | critical | [Bad credentials](#bad-credentials), [Provider 5xx](#provider-5xx) |
| `LlmUsageExporterPollFailures` | warning | [Provider 429](#provider-429--rate-limiting), [Provider 5xx](#provider-5xx), [Bad credentials](#bad-credentials) |
| `LlmUsageExporterStale` | warning | [Bad credentials](#bad-credentials) |
| `LlmUsageExporterSlowPoll` | info | [Provider 429](#provider-429--rate-limiting) (rate limit slowing responses) |
