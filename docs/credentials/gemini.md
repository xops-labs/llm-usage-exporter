# Google Gemini / Vertex AI credential setup

The `llm-usage-exporter` polls Cloud Monitoring for Vertex AI token usage and request counts, joined per `(start_time, end_time, model_id)`, and optionally queries a BigQuery billing-export dataset for spend. It needs a GCP service account with at most two IAM roles: `roles/monitoring.viewer` always, plus `roles/bigquery.dataViewer` and `roles/bigquery.jobUser` when cost queries are enabled. Authentication is either a service-account JSON keyfile (recommended) or a pre-issued OAuth2 access token (testing only).

## Least-privilege profile

- Use a dedicated GCP service account for this exporter and environment.
- Grant `roles/monitoring.viewer` only on the project that emits Vertex AI metrics.
- Enable BigQuery cost queries only when needed. If enabled, grant dataset-level read access to the billing export dataset and `roles/bigquery.jobUser` only on the project that runs the query.
- Do not grant `roles/owner`, `roles/editor`, `roles/monitoring.admin`, `roles/bigquery.admin`, or broad project-wide BigQuery data access.
- Store the service-account JSON keyfile in GCP Secret Manager, Vault, an external secret manager, or a Kubernetes Secret volume. Prefer a short-lived OAuth token only for local testing because the exporter does not refresh `GEMINI_ACCESS_TOKEN`.
- The exporter never intentionally logs the keyfile contents, private key, OAuth access token, or `Authorization` header. Provider error snippets are redacted before they reach logs, traces, or health state.

## Step 1 — Create a service account

Google Cloud Console flow:

1. Visit https://console.cloud.google.com/iam-admin/serviceaccounts and select the project that hosts Vertex AI usage.
2. Click **Create service account**.
3. Name: `llm-usage-exporter`.
4. ID: `llm-usage-exporter` (the account email becomes `llm-usage-exporter@<project>.iam.gserviceaccount.com`).
5. Click **Create and continue** — leave the optional "Grant this service account access to project" step empty; roles are bound explicitly in Step 2.
6. Click **Done**.

Alternative with `gcloud`:

```bash
PROJECT=my-gcp-project
gcloud config set project $PROJECT

gcloud iam service-accounts create llm-usage-exporter \
  --display-name "llm-usage-exporter"

SA_EMAIL=llm-usage-exporter@$PROJECT.iam.gserviceaccount.com
```

## Step 2 — Grant the minimum IAM roles

Two roles at most, both at project scope. Do not use `roles/owner`, `roles/editor`, or `roles/monitoring.admin` — the exporter only reads.

### Role 1: Monitoring Viewer (always required)

- Permission needed: `monitoring.timeSeries.list`
- Role: **roles/monitoring.viewer**
- Scope: the project that emits Vertex AI metrics

```bash
gcloud projects add-iam-policy-binding $PROJECT \
  --member="serviceAccount:$SA_EMAIL" \
  --role="roles/monitoring.viewer"
```

### Role 2: BigQuery Data Viewer + Job User (only when cost queries are enabled)

Skip this section if you will not set `GEMINI_ENABLE_COST_QUERIES=true`.

- Permission needed: read the billing-export dataset and run query jobs
- Roles: **roles/bigquery.dataViewer** on the dataset, **roles/bigquery.jobUser** on the project that runs the query
- Scope: the billing export dataset (frequently in a different "billing account project") and the query-runner project

Grant read on the dataset (note the billing dataset can live in a separate project):

```bash
BILLING_DATASET_PROJECT=billing-account-project    # may differ from $PROJECT
BILLING_DATASET=billing_export

bq add-iam-policy-binding \
  --project_id=$BILLING_DATASET_PROJECT \
  $BILLING_DATASET_PROJECT:$BILLING_DATASET \
  --member="serviceAccount:$SA_EMAIL" \
  --role="roles/bigquery.dataViewer"
```

Grant job-execution rights on the project that will run the query (usually `$PROJECT`):

```bash
gcloud projects add-iam-policy-binding $PROJECT \
  --member="serviceAccount:$SA_EMAIL" \
  --role="roles/bigquery.jobUser"
```

## Step 3 — Create and download a JSON key

1. Open the service account in the console, go to the **Keys** tab.
2. Click **Add key** → **Create new key** → **JSON** → **Create**.
3. The browser downloads `<sa-name>-<id>.json`. Treat it as a secret — it is a long-lived credential.
4. Upload it to your secrets manager (GCP Secret Manager, HashiCorp Vault, AWS Secrets Manager, sealed-secrets, etc.). Never commit it to git.
5. Mount it into the exporter container at a known path, for example `/secrets/gemini-sa.json`.

Helm shortcut — paste the keyfile contents inline as `gemini.serviceAccountKey` in `values.yaml` and the chart creates a dedicated Secret and mounts it for you:

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
      "client_email": "llm-usage-exporter@my-gcp-project.iam.gserviceaccount.com",
      "client_id": "...",
      "token_uri": "https://oauth2.googleapis.com/token"
    }
```

## Step 4 — Configure the exporter

Two authentication modes. Pick one.

### Mode A — JSON keyfile (recommended)

The exporter does a hand-rolled JWT-bearer exchange against `https://oauth2.googleapis.com/token`, caches the access token, and refreshes before expiry.

```bash
GEMINI_PROJECT_ID=my-gcp-project
GEMINI_SERVICE_ACCOUNT_KEY_FILE=/secrets/gemini-sa.json
GEMINI_MODELS=gemini-1.5-pro,gemini-1.5-flash      # optional csv filter
```

### Mode B — Pre-issued OAuth2 access token

Useful only for local testing — Google OAuth2 access tokens expire after one hour and the exporter will not refresh them.

```bash
GEMINI_PROJECT_ID=my-gcp-project
GEMINI_ACCESS_TOKEN=$(gcloud auth print-access-token)
```

### Cost queries (optional)

Enable only if BigQuery billing export is already configured at the billing-account level.

```bash
GEMINI_ENABLE_COST_QUERIES=true
GEMINI_BILLING_PROJECT_ID=my-gcp-project                              # project that runs the BigQuery job
GEMINI_BILLING_DATASET_PROJECT=billing-account-project                # project that owns the dataset
GEMINI_BILLING_DATASET_ID=billing_export
GEMINI_BILLING_TABLE=gcp_billing_export_v1_XXXXXX_XXXXXX_XXXXXX
```

Helm `values.yaml` equivalent for the full `gemini` block:

```yaml
gemini:
  enabled: true
  projectId: my-gcp-project
  serviceAccountKey: |
    { "type": "service_account", ... }
  models:
    - gemini-1.5-pro
    - gemini-1.5-flash
  cost:
    enabled: true
    billingProjectId: my-gcp-project
    billingDatasetProject: billing-account-project
    billingDatasetId: billing_export
    billingTable: gcp_billing_export_v1_XXXXXX_XXXXXX_XXXXXX
```

## Step 5 — Verify

Confirm the keyfile mints a valid token:

```bash
gcloud auth activate-service-account --key-file=/secrets/gemini-sa.json
gcloud auth print-access-token
```

Then call Cloud Monitoring directly with that token:

```bash
TOKEN=$(gcloud auth print-access-token)
PROJECT=my-gcp-project
NOW=$(date -u +%s)
HOUR_AGO=$((NOW - 3600))

curl -sS "https://monitoring.googleapis.com/v3/projects/$PROJECT/timeSeries?filter=metric.type%3D%22aiplatform.googleapis.com%2Fpublisher%2Fonline_serving%2Ftoken_count%22&interval.startTime=$(date -u -d @$HOUR_AGO +'%Y-%m-%dT%H:%M:%SZ')&interval.endTime=$(date -u -d @$NOW +'%Y-%m-%dT%H:%M:%SZ')" \
  -H "Authorization: Bearer $TOKEN"
```

A success response is a JSON object with a `timeSeries` array (or an empty body if there has been no Vertex AI traffic in the window). Common failure shapes:

- `401 ACCESS_TOKEN_TYPE_UNSUPPORTED` — the bearer was a raw API key or a service-account key file rather than an OAuth2 access token.
- `403 PERMISSION_DENIED` on `monitoring.timeSeries.list` — the service account does not have `roles/monitoring.viewer`.
- `404 NOT_FOUND` — `GEMINI_PROJECT_ID` is wrong or the service account has no access to that project.

Confirm the exporter itself is polling:

```bash
curl -s http://localhost:8080/metrics | grep 'llm_exporter_poll_success_total{.*provider="gemini"'
```

The counter should increment on every poll interval. If `llm_exporter_poll_failure_total{provider="gemini"}` is climbing instead, check the exporter logs for the underlying Google API error.

## Rotation

GCP service-account keys do not auto-expire but should still be rotated on a schedule (90 days is a reasonable default).

1. In the service-account **Keys** tab, **Add key** → **Create new key** → **JSON**. A service account can hold multiple active keys.
2. Upload the new key to the secrets manager and update the exporter's mounted secret.
3. Restart the exporter pod and confirm `llm_exporter_poll_success_total{provider="gemini"}` resumes incrementing.
4. Once verified, delete the old key from the **Keys** tab.

For zero-touch rotation on GKE, Workload Identity is the preferred platform pattern, but the current exporter does not use Google Application Default Credentials directly. To avoid a JSON keyfile today, use a controlled token-injection flow that provides a fresh `GEMINI_ACCESS_TOKEN` at startup and restarts the exporter before it expires, or track the roadmap for native workload-identity support.

## Common errors

- `401 ACCESS_TOKEN_TYPE_UNSUPPORTED` — `GEMINI_ACCESS_TOKEN` is not an OAuth2 bearer token. A raw GCP API key will not work; only the output of `gcloud auth print-access-token` (or a JWT-bearer exchange) is accepted.
- `403 monitoring.timeSeries.list permission denied` — the service account is missing `roles/monitoring.viewer` on `GEMINI_PROJECT_ID`.
- `404 Project ... not found or you do not have access` — `GEMINI_PROJECT_ID` is wrong, or the project is in a different organization the SA cannot reach.
- `400 INVALID_ARGUMENT` from BigQuery — `GEMINI_BILLING_DATASET_PROJECT`, `GEMINI_BILLING_DATASET_ID`, or `GEMINI_BILLING_TABLE` does not match the actual export. Run `bq ls $BILLING_DATASET_PROJECT:$BILLING_DATASET` to list the real table name (it embeds the billing-account ID).
- Cost queries return silently empty rows — BigQuery billing export has not been enabled at the billing-account level. Turn it on at https://console.cloud.google.com/billing and wait several hours for the first rows to populate.
- `403 BigQuery: Permission bigquery.jobs.create denied` — missing `roles/bigquery.jobUser` on `GEMINI_BILLING_PROJECT_ID`.

## References

- [Service account creation](https://cloud.google.com/iam/docs/service-accounts-create)
- [Monitoring Viewer role](https://cloud.google.com/monitoring/access-control#mon_roles_desc)
- [Vertex AI Cloud Monitoring metrics](https://cloud.google.com/vertex-ai/docs/monitoring/monitoring)
- [Enable BigQuery billing export](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-setup)
- [BigQuery billing export schema](https://cloud.google.com/billing/docs/how-to/export-data-bigquery-tables/standard-usage)
- [Workload Identity for GKE](https://cloud.google.com/kubernetes-engine/docs/how-to/workload-identity)
