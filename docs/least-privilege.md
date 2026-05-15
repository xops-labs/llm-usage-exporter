# Least-privilege credentials

The exporter only needs read access to usage and billing data. It does not need to create, modify, or delete any cloud resources, invoke models, or administer provider accounts. This page documents the exact minimum permissions for each provider, how to create a dedicated credential with only those permissions, and how to verify the scope is correct.

Use provider filters (`OPENAI_PROJECT_IDS`, `ANTHROPIC_WORKSPACE_IDS`, `BEDROCK_MODEL_IDS`, etc.) to further narrow the data the exporter requests. These are observability boundaries, not authorization boundaries — a compromised credential can still call the API; filters only reduce what it asks for and what appears in metrics.

---

## OpenAI

**What the exporter calls:**
- `GET /v1/organizations/usage/completions`
- `GET /v1/organizations/usage/embeddings`
- `GET /v1/organizations/usage/images`
- `GET /v1/organizations/costs`

**Minimum credential type:** Organization admin API key (`sk-admin-...`). Regular user keys (`sk-...`) cannot read organization-level usage or costs — OpenAI enforces this at the API layer.

**Minimum key permissions:** The OpenAI admin key type does not have granular per-endpoint scope controls; any valid admin key can reach all admin endpoints. The blast radius is therefore the full organization's usage/cost read surface.

**Reduce blast radius:**
- Create a dedicated key named `llm-usage-exporter-prod` (not shared with other tools or humans).
- Rotate per environment (dev/staging/prod each have their own key).
- Set `OPENAI_PROJECT_IDS` so the exporter only requests data for production projects.
- OpenAI logs admin API key usage — review the audit log periodically at [platform.openai.com/settings/organization/admin-keys](https://platform.openai.com/settings/organization/admin-keys).

**Key creation:** See [docs/credentials/openai.md](credentials/openai.md).

---

## Anthropic Claude

**What the exporter calls:**
- `GET /v1/organizations/usage`
- `GET /v1/organizations/usage/models`
- `GET /v1/organizations/costs`

**Minimum credential type:** Organization admin API key (`sk-ant-admin-...`). Regular inference keys cannot read usage or cost data.

**Reduce blast radius:**
- Dedicated key per environment named `llm-usage-exporter-prod`.
- Set `ANTHROPIC_WORKSPACE_IDS` to limit data scope to specific workspaces.
- Set `ANTHROPIC_API_KEY_IDS` to limit usage data to specific API keys.

**Key creation:** See [docs/credentials/anthropic.md](credentials/anthropic.md).

---

## Azure OpenAI

**What the exporter calls:**
- Azure Monitor Metrics API — reads token/request counts from `microsoft.cognitiveservices/accounts`
- Azure Cost Management — reads cost rows filtered to `microsoft.cognitiveservices` provider

**Minimum RBAC roles:**

| Scope | Role | Purpose |
|---|---|---|
| Each Azure OpenAI account (resource) | `Monitoring Reader` | Read Azure Monitor metrics for that account |
| Resource group or subscription | `Cost Management Reader` | Read cost data scoped to the CognitiveServices provider |

Do not grant `Contributor`, `Owner`, `Cognitive Services User`, or any role that permits model inference, resource modification, or key generation.

**Creating the service principal:**

```bash
# 1. Create the app registration and service principal
az ad sp create-for-rbac \
  --name llm-usage-exporter-prod \
  --skip-assignment

# Output includes: appId (clientId), password (clientSecret), tenant (tenantId)
# Store password immediately — Azure shows it only once.

TENANT_ID="<tenantId from output>"
CLIENT_ID="<appId from output>"
CLIENT_SECRET="<password from output>"

# 2. Assign Monitoring Reader on each Azure OpenAI account resource
RESOURCE_ID="/subscriptions/<sub>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account>"
az role assignment create \
  --assignee "$CLIENT_ID" \
  --role "Monitoring Reader" \
  --scope "$RESOURCE_ID"

# 3. Assign Cost Management Reader at the narrowest useful scope
# (resource group if all OpenAI accounts live there, otherwise subscription)
SCOPE="/subscriptions/<sub>/resourceGroups/<rg>"
az role assignment create \
  --assignee "$CLIENT_ID" \
  --role "Cost Management Reader" \
  --scope "$SCOPE"

# 4. Verify — the principal should be able to read metrics but not list keys
az rest \
  --method GET \
  --uri "https://management.azure.com${RESOURCE_ID}/providers/microsoft.insights/metrics?api-version=2023-10-01&metricnames=SuccessfulCalls" \
  --headers "Authorization=Bearer $(az account get-access-token --query accessToken -o tsv)"
```

**Credential client secret expiry:** Azure client secrets expire (default 6–24 months). Set the shortest expiry your rotation process can sustain. Use Microsoft Entra certificate credentials instead of client secrets for longer-lived automation principals — certificates do not expire on a fixed schedule and the private key never leaves your vault.

**Key creation:** See [docs/credentials/azure-openai.md](credentials/azure-openai.md).

---

## Google Gemini / Vertex AI

**What the exporter calls:**
- Google Cloud Monitoring API — reads token/request time-series from `aiplatform.googleapis.com`
- BigQuery API — queries billing export (only when `GEMINI_ENABLE_COST_QUERIES=true`)

**Minimum IAM roles:**

| Condition | Role | Scope |
|---|---|---|
| Always | `roles/monitoring.viewer` | GCP project hosting Vertex AI usage |
| Cost queries enabled | `roles/bigquery.dataViewer` | Billing export dataset only |
| Cost queries enabled | `roles/bigquery.jobUser` | Billing project (to run query jobs) |

Do not grant `roles/owner`, `roles/editor`, `roles/monitoring.admin`, `roles/bigquery.admin`, or any role that permits modifying resources or running models.

**Creating the service account:**

```bash
PROJECT_ID="my-gcp-project"
SA_NAME="llm-usage-exporter"
SA_EMAIL="${SA_NAME}@${PROJECT_ID}.iam.gserviceaccount.com"

# 1. Create the service account
gcloud iam service-accounts create "$SA_NAME" \
  --display-name "llm-usage-exporter (read-only usage metrics)" \
  --project "$PROJECT_ID"

# 2. Grant Monitoring Viewer on the project
gcloud projects add-iam-policy-binding "$PROJECT_ID" \
  --member "serviceAccount:${SA_EMAIL}" \
  --role "roles/monitoring.viewer"

# 3a. Cost queries: grant dataViewer on the billing dataset only (not project-wide)
BILLING_PROJECT="my-billing-project"
BILLING_DATASET="billing_export"
bq query --use_legacy_sql=false \
  "GRANT \`roles/bigquery.dataViewer\`
   ON SCHEMA \`${BILLING_PROJECT}.${BILLING_DATASET}\`
   TO 'serviceAccount:${SA_EMAIL}'"

# 3b. Cost queries: grant jobUser on the billing project (needed to execute queries)
gcloud projects add-iam-policy-binding "$BILLING_PROJECT" \
  --member "serviceAccount:${SA_EMAIL}" \
  --role "roles/bigquery.jobUser"

# 4. Download the JSON keyfile (for non-GKE deployments)
gcloud iam service-accounts keys create gemini-sa.json \
  --iam-account "$SA_EMAIL"
# Store gemini-sa.json in your secret manager — never commit it to git.

# 5. GKE Workload Identity (preferred — no keyfile needed)
gcloud iam service-accounts add-iam-policy-binding "$SA_EMAIL" \
  --role "roles/iam.workloadIdentityUser" \
  --member "serviceAccount:${PROJECT_ID}.svc.id.goog[llm-monitoring/llm-usage-exporter]"
# Then annotate the Kubernetes ServiceAccount:
#   iam.gke.io/gcp-service-account: llm-usage-exporter@my-gcp-project.iam.gserviceaccount.com
```

**Verify the scope:**

```bash
# Should succeed (monitoring.viewer)
gcloud monitoring time-series list \
  --filter='metric.type="aiplatform.googleapis.com/prediction/input_token_count"' \
  --project "$PROJECT_ID" \
  --impersonate-service-account "$SA_EMAIL"

# Should fail (no broader project access)
gcloud compute instances list \
  --project "$PROJECT_ID" \
  --impersonate-service-account "$SA_EMAIL"
# Expected: PERMISSION_DENIED
```

**Key creation:** See [docs/credentials/gemini.md](credentials/gemini.md).

---

## AWS Bedrock

**What the exporter calls:**
- CloudWatch `GetMetricData` — reads token/request metrics from `AWS/Bedrock` namespace
- Cost Explorer `GetCostAndUsage` — reads cost rows filtered to `AmazonBedrock` service (only when `BEDROCK_ENABLE_COST_QUERIES=true`)

**Minimum IAM policy:**

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "CloudWatchBedrockMetrics",
      "Effect": "Allow",
      "Action": [
        "cloudwatch:GetMetricData"
      ],
      "Resource": "*",
      "Condition": {
        "StringEquals": {
          "cloudwatch:namespace": "AWS/Bedrock"
        }
      }
    },
    {
      "Sid": "CostExplorerReadOnly",
      "Effect": "Allow",
      "Action": [
        "ce:GetCostAndUsage"
      ],
      "Resource": "*"
    }
  ]
}
```

Omit the `CostExplorerReadOnly` statement if you set `BEDROCK_ENABLE_COST_QUERIES=false`.

**Do not grant:** `AmazonBedrockFullAccess`, `AmazonBedrockReadOnlyAccess` (includes `bedrock:InvokeModel`), `ReadOnlyAccess` (too broad), `AdministratorAccess`.

**Creating the IAM role for IRSA (EKS):**

```bash
ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
CLUSTER_NAME="my-eks-cluster"
REGION="us-east-1"
NAMESPACE="llm-monitoring"
SA_NAME="llm-usage-exporter"
ROLE_NAME="llm-usage-exporter-prod"

# 1. Get the OIDC provider URL for the cluster
OIDC_URL=$(aws eks describe-cluster \
  --name "$CLUSTER_NAME" \
  --query "cluster.identity.oidc.issuer" \
  --output text | sed 's|https://||')

# 2. Create the trust policy
cat > trust-policy.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Principal": {
        "Federated": "arn:aws:iam::${ACCOUNT_ID}:oidc-provider/${OIDC_URL}"
      },
      "Action": "sts:AssumeRoleWithWebIdentity",
      "Condition": {
        "StringEquals": {
          "${OIDC_URL}:sub": "system:serviceaccount:${NAMESPACE}:${SA_NAME}",
          "${OIDC_URL}:aud": "sts.amazonaws.com"
        }
      }
    }
  ]
}
EOF

# 3. Create the IAM role and attach the policy
aws iam create-role \
  --role-name "$ROLE_NAME" \
  --assume-role-policy-document file://trust-policy.json

aws iam put-role-policy \
  --role-name "$ROLE_NAME" \
  --policy-name llm-usage-exporter-bedrock \
  --policy-document file://bedrock-policy.json   # the JSON from above

# 4. Annotate the Kubernetes ServiceAccount
kubectl annotate serviceaccount "$SA_NAME" \
  --namespace "$NAMESPACE" \
  "eks.amazonaws.com/role-arn=arn:aws:iam::${ACCOUNT_ID}:role/${ROLE_NAME}"
```

**Verify the scope:**

```bash
# Assume the role
CREDS=$(aws sts assume-role \
  --role-arn "arn:aws:iam::${ACCOUNT_ID}:role/${ROLE_NAME}" \
  --role-session-name verify-scope)

export AWS_ACCESS_KEY_ID=$(echo $CREDS | jq -r .Credentials.AccessKeyId)
export AWS_SECRET_ACCESS_KEY=$(echo $CREDS | jq -r .Credentials.SecretAccessKey)
export AWS_SESSION_TOKEN=$(echo $CREDS | jq -r .Credentials.SessionToken)

# Should succeed
aws cloudwatch get-metric-data \
  --metric-data-queries '[{"Id":"test","MetricStat":{"Metric":{"Namespace":"AWS/Bedrock","MetricName":"InputTokenCount"},"Period":3600,"Stat":"Sum"}}]' \
  --start-time $(date -u -d '1 hour ago' +%Y-%m-%dT%H:%M:%SZ) \
  --end-time $(date -u +%Y-%m-%dT%H:%M:%SZ)

# Should fail (no Bedrock invoke permission)
aws bedrock-runtime invoke-model \
  --model-id anthropic.claude-3-haiku-20240307-v1:0 \
  --body '{"prompt":"test"}' \
  /dev/null 2>&1
# Expected: AccessDeniedException
```

**Static IAM user keys (non-EKS only):** If you cannot use IRSA, create a dedicated IAM user with only the above policy, generate an access key, and store it in your secret manager. Rotate static keys on a maximum 90-day cycle. Do not use long-lived static keys in production EKS — IRSA is available in all EKS regions.

**Key creation:** See [docs/credentials/aws-bedrock.md](credentials/aws-bedrock.md).

---

## OTLP backend credentials

If `OTEL_EXPORTER_OTLP_HEADERS` contains an API key for a managed backend (Grafana Cloud, Honeycomb, Datadog), treat it with the same controls as provider credentials:

- Store in the same Kubernetes Secret as provider credentials.
- Use the minimum write-only ingest scope for that backend (not admin, not query, not dashboard-edit).
- Rotate independently of provider credentials so a compromised provider key doesn't also expose telemetry data.

---

## Kubernetes RBAC for the ServiceAccount

The exporter pod does not call the Kubernetes API, so its ServiceAccount needs no ClusterRole or Role binding. The default empty role is correct.

If your platform requires explicit deny policies, create a `ClusterRole` with no rules and bind it to the ServiceAccount. This makes the "no Kubernetes API access" intent visible in audit logs:

```yaml
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: llm-usage-exporter-no-k8s-api
rules: []   # no Kubernetes API access required

---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: llm-usage-exporter-no-k8s-api
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: llm-usage-exporter-no-k8s-api
subjects:
  - kind: ServiceAccount
    name: llm-usage-exporter
    namespace: llm-monitoring
```

## See also

- [docs/secret-delivery.md](secret-delivery.md) — Kubernetes Secret YAML and external secret manager examples
- [docs/tenant-isolation.md](tenant-isolation.md) — what is isolated per registration, blast-radius table
- [docs/security-secrets.md](security-secrets.md) — non-logging guarantee, rotation pattern, log redaction list
