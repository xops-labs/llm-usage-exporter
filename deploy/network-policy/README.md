# Network security artifacts

This directory contains ready-to-apply network isolation artifacts for the
exporter. Apply the ones that match your cluster's CNI and service-mesh setup.

| File | Use when |
|---|---|
| `exporter-netpol.yaml` | Any CNI that enforces `NetworkPolicy` (Calico, Cilium, Weave) |
| `prometheus-netpol.yaml` | Companion rule to add to Prometheus's own namespace |
| `istio-authpolicy.yaml` | Istio service mesh with mTLS; replaces or augments NetworkPolicy |

## Quick start

```bash
# Label the namespaces so selectors resolve correctly
kubectl label namespace llm-monitoring kubernetes.io/metadata.name=llm-monitoring
kubectl label namespace monitoring       kubernetes.io/metadata.name=monitoring

# Apply NetworkPolicy (works with any policy-enforcing CNI)
kubectl apply -f exporter-netpol.yaml    --namespace llm-monitoring
kubectl apply -f prometheus-netpol.yaml  --namespace monitoring

# Apply Istio AuthorizationPolicy (Istio clusters only)
kubectl apply -f istio-authpolicy.yaml   --namespace llm-monitoring
```

## What is protected

The exporter exposes three surfaces that must not be reachable from arbitrary pods:

| Endpoint | Data | Who should reach it |
|---|---|---|
| `/metrics` | Token/cost metrics, possibly with `tenancy_id` | Prometheus only |
| `/focus.csv` `/focus.json` | Full FOCUS v1.0 billing export | Authorised FinOps consumers only |
| `/health` | Aggregate health state, no credentials | Any internal health-check |

The Kubernetes API is not called by the exporter pod — no RBAC binding is needed.

## NetworkPolicy scope

`exporter-netpol.yaml` enforces:

**Ingress:** Only pods with `app.kubernetes.io/name=prometheus` in the `monitoring` namespace can connect to port 8080. All other inbound connections are dropped.

**Egress:** Only TCP/443 (provider HTTPS APIs), TCP/4317-4318 (OTLP, if enabled), and DNS (UDP+TCP/53) are permitted. No pod-to-pod, node-to-pod, or cross-cluster egress is allowed.

Adjust the port list if your OTLP collector or provider proxy uses a non-standard port.

## Istio scope

`istio-authpolicy.yaml` enforces mTLS STRICT on the exporter pod and then:

1. **Deny-all** as the default.
2. **Allow Prometheus** (by ServiceAccount principal) to GET `/metrics`.
3. **Allow unauthenticated** access to `/health` (kubelet probes carry no mTLS identity).
4. **Optional ALLOW** for FOCUS export consumers (commented out by default).

Replace `cluster.local/ns/monitoring/sa/prometheus` with your actual Prometheus ServiceAccount SPIFFE identity.

## Combining NetworkPolicy and Istio

NetworkPolicy and Istio operate at different layers. NetworkPolicy restricts at the network (L3/L4) level; Istio AuthorizationPolicy restricts at the application (L7) level. Using both is defence in depth:

- A misconfigured Istio rule cannot bypass NetworkPolicy.
- A misconfigured NetworkPolicy (e.g., too-broad egress) cannot bypass Istio mTLS.

Apply both sets of manifests in Istio-enabled clusters.

## Helm chart integration

The Helm chart optionally renders a `NetworkPolicy` based on `values.yaml`:

```yaml
networkPolicy:
  enabled: true
  prometheusNamespace: monitoring     # namespace where Prometheus runs
  prometheusSelector:
    app.kubernetes.io/name: prometheus
  additionalIngressRules: []          # extra ingress rules
  additionalEgressPorts: []           # extra egress ports (e.g., 4317 for OTLP)
```

The Helm template produces the same policy as `exporter-netpol.yaml`. Use the
standalone YAML files if you manage network policies separately from Helm releases.

## See also

- [docs/tenant-isolation.md](../../docs/tenant-isolation.md) — what is isolated per tenant and blast-radius analysis
- [docs/secret-delivery.md](../../docs/secret-delivery.md) — how to deliver credentials via external secret managers
- [docs/least-privilege.md](../../docs/least-privilege.md) — per-provider IAM minimum permissions
