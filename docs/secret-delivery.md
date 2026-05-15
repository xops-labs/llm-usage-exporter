# Secret delivery

The exporter reads all provider credentials from environment variables at startup. This page shows how to deliver those variables safely — from a bare Kubernetes Secret through managed external secret stores — so that secrets never live in plaintext in Helm values files, container images, or source code.

Every pattern ends in the same shape: a Kubernetes `Secret` object in the exporter's namespace that is consumed via `envFrom` in the Deployment. The patterns differ only in where the secret values originate and how rotation triggers a pod restart.

## What goes in the Secret

Sensitive fields only. Non-sensitive configuration (project IDs, region names, API versions, poll intervals, model lists) goes in the ConfigMap, not the Secret.

| Variable | Sensitive |
|---|---|
| `OPENAI_ADMIN_API_KEY` | Yes |
| `ANTHROPIC_ADMIN_API_KEY` | Yes |
| `AZURE_OPENAI_TENANT_ID` | Yes |
| `AZURE_OPENAI_CLIENT_ID` | Yes |
| `AZURE_OPENAI_CLIENT_SECRET` | Yes |
| `GEMINI_ACCESS_TOKEN` | Yes |
| `GEMINI_SERVICE_ACCOUNT_KEY_JSON` | Yes (inline keyfile) |
| `AWS_ACCESS_KEY_ID` | Yes |
| `AWS_SECRET_ACCESS_KEY` | Yes |
| `AWS_SESSION_TOKEN` | Yes (if using STS) |
| `OTEL_EXPORTER_OTLP_HEADERS` | Yes (if contains API key) |

---

## Pattern A — Plain Kubernetes Secret (baseline)

Suitable for local clusters, CI environments, and as the fall-through when no external manager is available. The values are base64-encoded — not encrypted — so you must enable etcd encryption at rest separately.

```yaml
# deploy/kubernetes/secret.yaml
# Do NOT commit this file to git with real values.
# Use it as a template and apply it from your CI/CD pipeline after substitution.
apiVersion: v1
kind: Secret
metadata:
  name: llm-usage-exporter-credentials
  namespace: llm-monitoring
  labels:
    app.kubernetes.io/name: llm-usage-exporter
    app.kubernetes.io/component: credentials
type: Opaque
stringData:                         # stringData: auto-encodes to base64 at apply time
  # ── OpenAI (comment out if not using OpenAI) ────────────────────────────
  OPENAI_ADMIN_API_KEY: "sk-admin-replace-me"

  # ── Anthropic (comment out if not using Anthropic) ──────────────────────
  ANTHROPIC_ADMIN_API_KEY: "sk-ant-admin-replace-me"

  # ── Azure OpenAI (comment out if not using Azure OpenAI) ────────────────
  AZURE_OPENAI_TENANT_ID: "00000000-0000-0000-0000-000000000000"
  AZURE_OPENAI_CLIENT_ID: "00000000-0000-0000-0000-000000000000"
  AZURE_OPENAI_CLIENT_SECRET: "replace-me"

  # ── Gemini (comment out if not using Gemini) ────────────────────────────
  # Use one of the two: keyfile JSON or short-lived token.
  # GEMINI_SERVICE_ACCOUNT_KEY_JSON: |
  #   { "type": "service_account", "project_id": "...", ... }
  # GEMINI_ACCESS_TOKEN: "ya29.replace-me"

  # ── Bedrock (comment out if not using Bedrock) ──────────────────────────
  AWS_ACCESS_KEY_ID: "AKIAreplace-me"
  AWS_SECRET_ACCESS_KEY: "replace-me"
  # AWS_SESSION_TOKEN: "replace-me-if-using-sts"

  # ── OTLP backend (comment out if no OTLP headers needed) ────────────────
  # OTEL_EXPORTER_OTLP_HEADERS: "api-key=replace-me"
```

Apply with value substitution, never with literal values committed to git:

```bash
# CI/CD: fetch from your secrets manager, then pipe into kubectl
aws secretsmanager get-secret-value \
  --secret-id llm-usage-exporter/prod \
  --query SecretString --output text \
  | kubectl create secret generic llm-usage-exporter-credentials \
      --namespace llm-monitoring \
      --from-env-file /dev/stdin \
      --dry-run=client -o yaml \
  | kubectl apply -f -
```

---

## Pattern B — External Secrets Operator (ESO)

[External Secrets Operator](https://external-secrets.io) reconciles secrets from an external store into Kubernetes `Secret` objects. The pod consumes the same `Secret` as Pattern A — ESO is only the delivery mechanism.

Install ESO before applying these manifests:

```bash
helm repo add external-secrets https://charts.external-secrets.io
helm install external-secrets external-secrets/external-secrets \
  --namespace external-secrets --create-namespace
```

### B1 — AWS Secrets Manager

```yaml
# SecretStore — once per namespace, references the AWS provider
apiVersion: external-secrets.io/v1beta1
kind: SecretStore
metadata:
  name: aws-secretsmanager
  namespace: llm-monitoring
spec:
  provider:
    aws:
      service: SecretsManager
      region: us-east-1
      auth:
        jwt:
          serviceAccountRef:
            name: llm-usage-exporter   # must have IRSA annotation
---
# ExternalSecret — maps AWS Secrets Manager entries to K8s Secret keys
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: llm-usage-exporter-credentials
  namespace: llm-monitoring
spec:
  refreshInterval: 1h                  # how often ESO polls the store
  secretStoreRef:
    name: aws-secretsmanager
    kind: SecretStore
  target:
    name: llm-usage-exporter-credentials
    creationPolicy: Owner
    # Restart pods that consume this Secret when it rotates
    template:
      metadata:
        annotations:
          kubectl.kubernetes.io/restartedAt: "{{ .ExternalSecret.Status.RefreshTime }}"
  data:
    - secretKey: OPENAI_ADMIN_API_KEY
      remoteRef:
        key: llm-usage-exporter/prod       # Secret name in Secrets Manager
        property: openai_admin_api_key     # JSON property in that secret
    - secretKey: ANTHROPIC_ADMIN_API_KEY
      remoteRef:
        key: llm-usage-exporter/prod
        property: anthropic_admin_api_key
    - secretKey: AWS_ACCESS_KEY_ID
      remoteRef:
        key: llm-usage-exporter/bedrock-prod
        property: access_key_id
    - secretKey: AWS_SECRET_ACCESS_KEY
      remoteRef:
        key: llm-usage-exporter/bedrock-prod
        property: secret_access_key
```

Create the secret in AWS Secrets Manager with all fields as a JSON object:

```bash
aws secretsmanager create-secret \
  --name llm-usage-exporter/prod \
  --secret-string '{
    "openai_admin_api_key": "sk-admin-...",
    "anthropic_admin_api_key": "sk-ant-admin-..."
  }'
```

### B2 — GCP Secret Manager

```yaml
apiVersion: external-secrets.io/v1beta1
kind: SecretStore
metadata:
  name: gcp-secretmanager
  namespace: llm-monitoring
spec:
  provider:
    gcpsm:
      projectID: my-gcp-project
      auth:
        workloadIdentity:
          clusterLocation: us-central1
          clusterName: my-gke-cluster
          serviceAccountRef:
            name: llm-usage-exporter   # must have Workload Identity annotation
---
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: llm-usage-exporter-credentials
  namespace: llm-monitoring
spec:
  refreshInterval: 1h
  secretStoreRef:
    name: gcp-secretmanager
    kind: SecretStore
  target:
    name: llm-usage-exporter-credentials
    creationPolicy: Owner
  data:
    - secretKey: OPENAI_ADMIN_API_KEY
      remoteRef:
        key: llm-usage-exporter-openai-key      # Secret name in GCP Secret Manager
        version: latest
    - secretKey: ANTHROPIC_ADMIN_API_KEY
      remoteRef:
        key: llm-usage-exporter-anthropic-key
        version: latest
    - secretKey: GEMINI_SERVICE_ACCOUNT_KEY_JSON
      remoteRef:
        key: llm-usage-exporter-gemini-sa
        version: latest
```

Create each secret in GCP:

```bash
echo -n "sk-admin-..." | \
  gcloud secrets create llm-usage-exporter-openai-key \
    --data-file=- \
    --project my-gcp-project

# Grant the ESO service account access
gcloud secrets add-iam-policy-binding llm-usage-exporter-openai-key \
  --member "serviceAccount:llm-usage-exporter@my-gcp-project.iam.gserviceaccount.com" \
  --role "roles/secretmanager.secretAccessor" \
  --project my-gcp-project
```

### B3 — Azure Key Vault

```yaml
apiVersion: external-secrets.io/v1beta1
kind: SecretStore
metadata:
  name: azure-keyvault
  namespace: llm-monitoring
spec:
  provider:
    azurekv:
      vaultUrl: "https://my-llm-exporter-kv.vault.azure.net"
      authType: WorkloadIdentity
      serviceAccountRef:
        name: llm-usage-exporter   # must have azure.workload.identity/client-id annotation
---
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: llm-usage-exporter-credentials
  namespace: llm-monitoring
spec:
  refreshInterval: 1h
  secretStoreRef:
    name: azure-keyvault
    kind: SecretStore
  target:
    name: llm-usage-exporter-credentials
    creationPolicy: Owner
  data:
    - secretKey: OPENAI_ADMIN_API_KEY
      remoteRef:
        key: llm-exporter-openai-key            # Key Vault secret name (hyphens, no underscores)
    - secretKey: AZURE_OPENAI_CLIENT_SECRET
      remoteRef:
        key: llm-exporter-azureopenai-client-secret
    - secretKey: AWS_SECRET_ACCESS_KEY
      remoteRef:
        key: llm-exporter-bedrock-secret-key
```

Grant the managed identity Key Vault Secrets User on the vault or per-secret:

```bash
az keyvault set-policy \
  --name my-llm-exporter-kv \
  --object-id <managed-identity-object-id> \
  --secret-permissions get list
```

### B4 — HashiCorp Vault

```yaml
apiVersion: external-secrets.io/v1beta1
kind: SecretStore
metadata:
  name: hashicorp-vault
  namespace: llm-monitoring
spec:
  provider:
    vault:
      server: "https://vault.internal.example.com"
      path: secret                          # KV v2 mount point
      version: v2
      auth:
        kubernetes:
          mountPath: kubernetes
          role: llm-usage-exporter
          serviceAccountRef:
            name: llm-usage-exporter
---
apiVersion: external-secrets.io/v1beta1
kind: ExternalSecret
metadata:
  name: llm-usage-exporter-credentials
  namespace: llm-monitoring
spec:
  refreshInterval: 15m
  secretStoreRef:
    name: hashicorp-vault
    kind: SecretStore
  target:
    name: llm-usage-exporter-credentials
    creationPolicy: Owner
  data:
    - secretKey: OPENAI_ADMIN_API_KEY
      remoteRef:
        key: llm-usage-exporter/prod
        property: openai_admin_api_key
    - secretKey: ANTHROPIC_ADMIN_API_KEY
      remoteRef:
        key: llm-usage-exporter/prod
        property: anthropic_admin_api_key
    - secretKey: AWS_ACCESS_KEY_ID
      remoteRef:
        key: llm-usage-exporter/bedrock
        property: access_key_id
    - secretKey: AWS_SECRET_ACCESS_KEY
      remoteRef:
        key: llm-usage-exporter/bedrock
        property: secret_access_key
```

Configure the Vault Kubernetes auth role:

```bash
vault write auth/kubernetes/role/llm-usage-exporter \
  bound_service_account_names=llm-usage-exporter \
  bound_service_account_namespaces=llm-monitoring \
  policies=llm-usage-exporter-read \
  ttl=1h

vault policy write llm-usage-exporter-read - <<EOF
path "secret/data/llm-usage-exporter/*" {
  capabilities = ["read"]
}
EOF
```

---

## Pattern C — Sealed Secrets (GitOps)

[Sealed Secrets](https://github.com/bitnami-labs/sealed-secrets) encrypts a Kubernetes `Secret` into a `SealedSecret` that is safe to commit to git. The controller in the cluster decrypts it.

```bash
# Seal the secret with the cluster's public key
kubeseal \
  --format yaml \
  --namespace llm-monitoring \
  --name llm-usage-exporter-credentials \
  < plain-secret.yaml \
  > sealed-secret.yaml

# Commit sealed-secret.yaml to git — the plain-secret.yaml must not be committed.
git add deploy/kubernetes/sealed-secret.yaml
```

Rotation: generate a new `SealedSecret` with the new values and `kubectl apply` it. The controller decrypts and updates the underlying `Secret`, which triggers the Deployment's checksum annotation to roll the pod.

---

## Rotation and pod restart

The exporter reads all credentials at startup and does not hot-reload them. Every rotation pattern must trigger a pod restart after the `Secret` is updated.

**Checksum annotation (Helm default):** The Helm Deployment template includes:

```yaml
annotations:
  checksum/secret: {{ include (print $.Template.BasePath "/secret.yaml") . | sha256sum }}
```

When the Secret changes and you run `helm upgrade`, the checksum changes, triggering a rolling restart.

**ESO with restartedAt annotation:** Add a template annotation to the `ExternalSecret` target that propagates the refresh timestamp into the Secret. The Deployment's `envFrom` reference reloads on pod restart, not on Secret update alone — you still need to roll the Deployment.

**Manual restart after rotation:**

```bash
kubectl rollout restart deployment/llm-usage-exporter \
  --namespace llm-monitoring

# Confirm the new pod is using the new credential
kubectl rollout status deployment/llm-usage-exporter \
  --namespace llm-monitoring --timeout=120s
```

Then verify polling resumed:

```bash
kubectl exec -n llm-monitoring deploy/llm-usage-exporter -- \
  curl -s http://localhost:8080/metrics \
  | grep llm_exporter_poll_success_total
```

---

## Helm: reference an existing Secret instead of inlining values

In production, do not put real credential values in `values.yaml`. Instead, create the `Secret` via one of the patterns above and tell the chart to use it:

```yaml
# values-production.yaml
openai:
  enabled: true
  adminApiKey: ""          # leave blank — populated by existingSecret below

# Point the chart's envFrom at a pre-existing Secret
existingSecret: llm-usage-exporter-credentials
```

If the chart does not yet expose an `existingSecret` field, patch the Deployment with `extraEnv` to reference individual keys from an existing Secret:

```yaml
extraEnv:
  - name: OPENAI_ADMIN_API_KEY
    valueFrom:
      secretKeyRef:
        name: llm-usage-exporter-credentials
        key: OPENAI_ADMIN_API_KEY
```

---

## See also

- [docs/least-privilege.md](least-privilege.md) — exact IAM permissions for each provider
- [docs/tenant-isolation.md](tenant-isolation.md) — what is isolated per registration and blast-radius analysis
- [docs/security-secrets.md](security-secrets.md) — non-logging guarantee, rotation pattern, log redaction list
- [deploy/network-policy/](../deploy/network-policy/) — Kubernetes NetworkPolicy templates restricting access to the metrics endpoint
