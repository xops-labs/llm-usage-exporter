# High availability and checkpoints

## Design principle: single-replica is correct

The exporter is a **polling sidecar**, not a request-serving service. It wakes up every `EXPORTER_POLL_INTERVAL_SECONDS` (default 300 s), reads from provider billing APIs, and publishes to in-process metric registries. The only state it holds is the checkpoint store — a seen-bucket set used to deduplicate repeated provider responses.

Running two replicas of the same tenant configuration **does not improve availability and will double-count metrics**. The `FileCheckpointStore` is local to one pod's PVC; replicas do not coordinate. Both will re-poll every bucket within the lookback window and advance Prometheus counters for the same spend twice. This is not a Kubernetes restart-race or a timing issue — it is a structural property of the polling model.

**Recommendation: run one replica.** Kubernetes will restart a failed pod within seconds. Prometheus's counter-reset heuristic (`increase()`) absorbs the brief gap transparently. A 10–30 s pod restart outage is not operationally meaningful for a metric whose data is already hours old from the provider side.

---

## Checkpoint stores

The checkpoint store is the mechanism that prevents double-counting when a provider re-emits previously published buckets (see [docs/failure-modes.md → Repeated cursor](failure-modes.md#repeated-cursor)).

### `InMemory` (default)

State lives in the process heap. Fast, zero I/O, zero config. Lost on pod restart.

**Trade-off:** After restart, the first poll re-publishes every bucket within the lookback window that the previous instance had already counted. Prometheus counters advance by those amounts again. Whether this matters depends on the lookback window size (`EXPORTER_LOOKBACK_MINUTES`, default 60 min) and whether your alerting rules use `rate()` / `increase()` (which are restart-tolerant) or raw counter values (which are not).

```env
CHECKPOINTS_PROVIDER=InMemory   # the default
```

### `File`

State is persisted to a JSONL file on disk. Survives pod restarts as long as the file is on a persistent volume (not the container's ephemeral filesystem).

```env
CHECKPOINTS_PROVIDER=File
CHECKPOINTS_FILE_PATH=/data/checkpoints.jsonl
CHECKPOINTS_MAX_ENTRIES=100000      # evicts oldest entries; default 100 000
CHECKPOINTS_RETENTION_HOURS=168    # entries older than this are expired; default 168 h
```

**Requirements:**

- Mount a `ReadWriteOnce` PVC at the configured path (see [docs/deployment.md → Step 2](deployment.md#step-2--author-the-production-valuesyaml)).
- The pod's `securityContext.fsGroup` must be able to write to the PVC.
- Only one replica may write to the file at a time. Do not use `ReadWriteMany` with `replicaCount > 1`.

**Sizing:** A JSONL entry is ~200 bytes. At `CHECKPOINTS_MAX_ENTRIES=100000` the file stays under 25 MiB. For most deployments a 1 Gi PVC is generous; 100 Mi is sufficient.

---

## Scaling out: shard tenants across deployments

When you need to poll more tenants than one replica can handle within the poll interval — or when you want pod-level blast-radius isolation per provider — the right pattern is **tenant sharding**: run multiple exporter deployments, each with a disjoint subset of `Tenants:Items`, each with its own PVC.

```
┌─────────────────────────────┐   ┌─────────────────────────────┐
│  llm-usage-exporter-a       │   │  llm-usage-exporter-b       │
│  Tenants: [team-a, team-b]  │   │  Tenants: [team-c, team-d]  │
│  PVC: checkpoints-a         │   │  PVC: checkpoints-b         │
└──────────────┬──────────────┘   └──────────────┬──────────────┘
               │                                  │
               └───────────┬──────────────────────┘
                           ▼
                      Prometheus
              (two scrape jobs, each targeting
               one deployment's Service)
```

Each deployment is independent: its own `values-<shard>.yaml`, its own Kubernetes Deployment and Service, its own `ServiceMonitor`. Prometheus scrapes both; the `tenant` label on every series identifies the origin without any special configuration.

**Helm pattern:**

```bash
# Shard A
helm upgrade --install llm-usage-exporter-a ./deploy/helm/llm-usage-exporter \
  -n observability -f values-shard-a.yaml

# Shard B
helm upgrade --install llm-usage-exporter-b ./deploy/helm/llm-usage-exporter \
  -n observability -f values-shard-b.yaml
```

Each `values-shard-*.yaml` overrides only `tenants.items` (restricting to that shard's tenants) and the PVC name to avoid collision.

---

## Process restart behavior

When a pod is restarted (eviction, rolling deployment, OOMKill):

1. The new pod starts, reads `checkpoints.jsonl` from the PVC, and loads all non-expired identities into memory.
2. The first poll deduplicates against the loaded checkpoint. Buckets seen before the restart are skipped — no double-counting.
3. Prometheus counters start fresh in the new process. Prometheus's `increase()` function handles the counter reset correctly.
4. Budget and anomaly state is rebuilt from scratch — one evaluation cycle after the first successful poll is normal.

**What can still cause one re-publication after restart:** If the pod crashed *during a poll cycle* (mid-write, between bucket processing and checkpoint persistence), the in-flight writes that were not flushed will be re-published on the next successful poll. The magnitude is bounded to at most one poll window's worth of buckets. `CheckpointFlushHostedService` flushes the checkpoint on `IHostedService.StopAsync`, so graceful shutdowns (rolling updates, SIGTERM) do not lose in-flight writes.

---

## External checkpoint store — roadmap

A **Redis-backed `ICheckpointStore`** is on the roadmap as the foundation for active-active deployments and cross-pod coordination. When shipped, it will allow:

- Multiple replicas of the same tenant configuration to share a seen-bucket set, preventing the double-counting that makes `replicaCount > 1` unsafe today.
- External checkpoint state that survives not just pod restarts but also PVC failure or node loss.
- TTL-based eviction managed by Redis rather than in-process logic.

Until the Redis store ships, **do not increase `replicaCount` on a single deployment**. The correct HA pattern today is tenant sharding (one shard per deployment, one replica each) plus Kubernetes pod restart recovery. Track the issue tracker for the Redis milestone before designing an active-active architecture around it.

---

## Summary: decision guide

| Scenario | Recommended configuration |
|---|---|
| Single team, one provider | `replicaCount: 1`, `InMemory` checkpoint |
| Production, restart durability needed | `replicaCount: 1`, `File` checkpoint on PVC |
| Many tenants / providers | Tenant sharding — multiple deployments, one per shard, each with `File` checkpoint |
| True active-active HA | Not supported today — wait for the Redis checkpoint store milestone |
| High poll frequency (< 60 s) | Not recommended; provider APIs impose rate limits and the billing data does not change faster than once per hour |

---

## See also

- [docs/deployment.md → Step 7](deployment.md#step-7--high-availability-and-the-multi-replica-caveat) — HA in the production deployment guide
- [docs/failure-modes.md → Process restart](failure-modes.md#process-restart) — restart behavior details
- [docs/failure-modes.md → Checkpoint write failure](failure-modes.md#checkpoint-write-failure) — what happens when the checkpoint cannot write
- [docs/configuration.md](configuration.md) — checkpoint store env vars
