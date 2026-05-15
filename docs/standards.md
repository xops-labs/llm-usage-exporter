# Standards and compliance

`llm-usage-exporter` targets the open standards and emerging frameworks driving the AI cost-observability space.

## Metrics and observability standards

| Standard | Body | Status in this project |
|---|---|---|
| **Prometheus exposition format** | CNCF Prometheus | Emitted at `/metrics` — primary surface |
| **OpenMetrics v1.0.0** | CNCF OpenMetrics | Forward-compatible (Prometheus is a strict subset) |
| **OpenTelemetry OTLP** | CNCF OpenTelemetry | Active — `llm.usage.*` instruments via OTLP gRPC + HTTP |
| **W3C Trace Context (`traceparent`)** | W3C | Active by default — provider HTTP calls propagate `traceparent` / `tracestate` unless `OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED=false` is set |
| **`/metrics` content-type negotiation** | Prometheus + OpenMetrics | Supported via `prometheus-net.AspNetCore` |

## FinOps and cost-reporting standards

| Standard | Body | What it gives us |
|---|---|---|
| **FOCUS — FinOps Open Cost & Usage Specification** | FinOps Foundation | Active — `/focus.csv` and `/focus.json` export the v1.0 schema for cross-provider rollups |
| **OpenAI Organization Usage API** | OpenAI | Authoritative per-bucket token and request data |
| **OpenAI Organization Costs API** | OpenAI | Authoritative USD spend, grouped by project / line item / API key |
| **CycloneDX / SPDX SBOM** | OWASP / Linux Foundation | Active — CycloneDX (source) and SPDX (container image) SBOMs are emitted by the release workflow, attached to the GitHub Release, and the SPDX SBOM is also pushed as a cosign attestation alongside the image |

## FOCUS version roadmap

### Current output — FOCUS 1.0 baseline

The exporter currently emits a **FOCUS 1.0 baseline** schema. Both `/focus.csv` and `/focus.json` respond with an `X-FOCUS-Version: 1.0` HTTP header so consumers can detect the schema version programmatically.

One FOCUS 1.1 column (`ServiceSubcategory`) is already included because it is natural to populate for LLM usage and costs no compatibility risk when added before any downstream has a fixed schema.

### Field gap — FOCUS 1.0 → 1.3

FOCUS 1.3 was ratified on December 4, 2025. The table below maps every column added after 1.0 against the current exporter implementation.

| Column | Introduced | Emitted today | Notes |
|---|---|---|---|
| `ServiceSubcategory` | 1.1 | ✅ `"Generative AI"` | Already included |
| `SkuId` | 1.1 | ❌ | For LLM: model identifier. Planned for FOCUS 1.3 output. |
| `SkuDescription` | 1.1 | ❌ | For LLM: human-readable model + service description. Planned for FOCUS 1.3 output. |
| `PricingQuantity` | 1.1 | ❌ | For LLM: total token count per bucket. Blocked on exposing token counts through cost buckets (currently only cost USD is available in `LlmCostBucket`). |
| `ListUnitPrice` | 1.1 | ❌ | Cost per pricing unit at list price. Blocked on same data model gap as `PricingQuantity`. |
| `CommitmentDiscountId` / `*Name` / `*Type` / `*Category` / `*Status` | 1.1 | N/A | Commitment-discount columns (reserved instances, savings plans). LLM token usage is on-demand; these fields do not apply and will remain absent. |
| `ChargeId` | 1.2 | ❌ | Provider-generated unique charge ID. For LLM: a deterministic hash of `(provider, tenancy_id, model, charge_period_start)`. Planned for FOCUS 1.3 output. |

### Roadmap

| Version | Status | What changes |
|---|---|---|
| **FOCUS 1.0** | **Current** — `X-FOCUS-Version: 1.0` | Baseline column set + `ServiceSubcategory` (1.1 backfill) + `x_provider_native_id` + `x_tenant_id` extensions |
| **FOCUS 1.3** | **Planned** | Adds `SkuId`, `SkuDescription`, `ChargeId` (deterministic hash); defers `PricingQuantity` and `ListUnitPrice` until token counts are available in cost buckets |

### Version-selectable export

Adding new columns to the existing `/focus.csv` and `/focus.json` endpoints is a **breaking change** for any downstream that has imported the schema into a warehouse via `CREATE TABLE … AS SELECT` or a fixed `COPY` statement. The current column set is therefore frozen at its current shape.

FOCUS 1.3 output will be served from **separate versioned endpoints**:

| Endpoint | Schema | Status |
|---|---|---|
| `/focus.csv` · `/focus.json` | FOCUS 1.0 baseline (current, frozen) | Active |
| `/focus/v1.3.csv` · `/focus/v1.3.json` | FOCUS 1.3 (adds `SkuId`, `SkuDescription`, `ChargeId`) | Planned — not shipped yet |

Until FOCUS 1.3 endpoints ship, migrate by detecting `X-FOCUS-Version: 1.0` on the response and adding the new columns as nullable in your warehouse schema when you upgrade.

## Importing FOCUS exports into downstream FinOps tools

The exporter publishes FOCUS v1.0 records at two pull endpoints:

| Endpoint | Format | When to use |
|---|---|---|
| `GET /focus.csv` | RFC 4180 CSV with the FOCUS v1.0 column order | Spreadsheet loaders, warehouse `COPY` statements, any tool that prefers CSV |
| `GET /focus.json` | JSON array of FOCUS records | API-style ingestion, jq pipelines, tools that auto-detect schema from JSON |

Both endpoints return the same record set — the full FOCUS v1.0 column set plus two `x_*` extension columns (`x_provider_native_id`, `x_tenant_id`). The exporter does *not* push these records anywhere; every FinOps tool below pulls on its own cadence.

### Cloudability (IBM Apptio)

Cloudability accepts custom cost data via its **Custom Cost** ingestion API or via FOCUS-formatted CSV in S3 (the FOCUS adapter is the cleaner path). Schedule a recurring fetch from your platform — a CronJob, an Airflow DAG, an EventBridge rule — that lands the CSV in an S3 prefix Cloudability already watches.

```bash
# Daily pull → S3 → Cloudability FOCUS adapter ingests on its native cadence
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.csv?tenant=default" \
  | aws s3 cp - "s3://acme-finops-focus/llm-usage-exporter/$(date -u +%Y/%m/%d)/focus.csv"
```

Once landed, the records appear alongside cloud spend in the same FOCUS schema — `ServiceCategory=AI and Machine Learning` + `ServiceSubcategory=Generative AI` is how to filter to just LLM spend in views and budgets.

### Vantage

Vantage supports two ingestion paths for FOCUS data: the **Custom Providers** importer (UI-driven, points at an S3 / GCS bucket) and the **Vantage API** (programmatic). Both consume the standard FOCUS v1.0 schema directly. Use the same daily-pull-to-S3 pattern shown for Cloudability above and point a Vantage Custom Provider at the same bucket.

For per-pull push to the Vantage API:

```bash
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.json" \
  | curl -sf -X POST "https://api.vantage.sh/v2/costs/imports" \
    -H "Authorization: Bearer ${VANTAGE_API_TOKEN}" \
    -H "Content-Type: application/json" \
    --data-binary @-
```

Map the `Tenant` / `x_tenant_id` columns onto your Vantage **business mappings** to drive showback / chargeback per team.

### Apptio (ApptioOne / TBM)

Apptio's TBM Studio and ApptioOne both ingest CSV via the **Datalink** agent or via an SFTP drop. Schedule a daily `/focus.csv` pull into the SFTP directory your Apptio environment polls:

```bash
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.csv" \
  | sftp -b - apptio-uploads@sftp.apptio.example.com <<EOF
put /dev/stdin /incoming/llm-usage-exporter/focus-$(date -u +%Y%m%d).csv
EOF
```

Configure the Datalink job to use the FOCUS v1.0 column mapping (Apptio ships a template). The `ChargePeriodStart` / `ChargePeriodEnd` columns drive Apptio's calendar bucketing automatically.

### OpenCost

OpenCost reads cost data from cloud providers natively but accepts **Custom Cost Sources** via its `--custom-cost-config` flag. The cleanest integration is to land FOCUS records as a CSV file in a ConfigMap or PVC that OpenCost mounts, then point the custom-cost reader at it:

```yaml
# values.yaml for the OpenCost Helm chart
opencost:
  customPricing:
    enabled: true
    configMap: llm-usage-focus
extraVolumes:
  - name: llm-usage-focus
    configMap:
      name: llm-usage-focus
extraVolumeMounts:
  - name: llm-usage-focus
    mountPath: /var/opencost/custom
```

Refresh the ConfigMap on a schedule with a tiny CronJob that pulls `/focus.csv` and applies it with `kubectl create configmap --dry-run=client -o yaml | kubectl apply -f -`. OpenCost picks up the change on its next reconciliation cycle. Once loaded, LLM spend joins compute / storage spend under the same allocation API.

### Finout

Finout supports **Custom Cost Streams** that consume FOCUS-shaped CSV / JSON via webhook or via an S3 drop with a Finout-managed IAM role. The S3 path is identical to Cloudability above. For the webhook path:

```bash
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.json" \
  | curl -sf -X POST "${FINOUT_INGEST_WEBHOOK_URL}" \
    -H "X-Finout-Source: llm-usage-exporter" \
    -H "Content-Type: application/json" \
    --data-binary @-
```

Map the `ServiceName` (provider id) and `ResourceName` (model id) onto Finout **cost-center** rules to drive per-team allocations.

### CloudZero AnyCost

CloudZero's **AnyCost Adapter** ingests cost data via either S3 drop or REST endpoint, and ships with native FOCUS schema support. The S3 path is identical to Cloudability's; for the REST path, point AnyCost's connector at a small relay that proxies `/focus.json`:

```bash
# Run as a CronJob; CloudZero polls the relay on its own schedule.
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.json"
```

### Direct warehouse ingest (BigQuery / Snowflake / Redshift / Athena)

If your FinOps stack is built on a warehouse rather than a SaaS, treat `/focus.csv` as a source file and `COPY` it directly. The FOCUS v1.0 column set is stable, so a `CREATE TABLE ... AS SELECT` against the first import produces a usable schema you can append into.

**BigQuery:**

```bash
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.csv" \
  | gsutil cp - "gs://acme-finops/focus/llm/$(date -u +%Y%m%d).csv"

bq load --source_format=CSV --skip_leading_rows=1 --autodetect \
  acme_finops.llm_usage_focus \
  "gs://acme-finops/focus/llm/*.csv"
```

**Snowflake:**

```sql
COPY INTO acme_finops.llm_usage_focus
FROM @acme_finops_stage/focus/llm/
FILE_FORMAT = (TYPE = CSV SKIP_HEADER = 1 FIELD_OPTIONALLY_ENCLOSED_BY = '"')
PATTERN = '.*\.csv';
```

**Redshift:**

```sql
COPY acme_finops.llm_usage_focus
FROM 's3://acme-finops/focus/llm/'
IAM_ROLE 'arn:aws:iam::123456789012:role/redshift-finops'
CSV IGNOREHEADER 1;
```

**Athena (external table over S3 + Glue):** create the external table once against the FOCUS column set, drop new CSVs into the partitioned S3 prefix daily, and `MSCK REPAIR TABLE acme_finops.llm_usage_focus;`. Once landed, join LLM spend against your CUR / billing exports on `BillingAccountId` and `ChargePeriodStart` in the same dialect.

### Notes for every downstream

- **Schedule the pull, don't poll hard.** A daily 24h-rollup pull is enough for monthly chargeback; an hourly pull is enough for daily showback. Hammering `/focus.json` every minute is wasteful — the records change at provider-billing cadence, not exporter-scrape cadence.
- **Authenticate the endpoint.** In multi-tenant mode, `/focus.csv` and `/focus.json` honor the same `Tenants:ApiKeys` bearer tokens as `/metrics`. Never expose them unauthenticated to a SaaS ingest webhook.
- **The `Tenant` and `x_tenant_id` columns are your join key.** Map them to your downstream cost-center / business-unit dimension; everything else (model, region, provider) is metadata for filtering.
- **Provider invoices remain the source of record.** The FOCUS export reflects the same operational signal as `/metrics` — rounding, credits, discounts, and invoice adjustments still belong to the provider's bill of record. See [README → Why it matters](../README.md#why-it-matters).

## Engineering and supply-chain standards

- **Apache License 2.0** — permissive, OSI-approved, allows commercial use, modification, and distribution.
- **Semantic Versioning 2.0.0** — every release follows `vMAJOR.MINOR.PATCH`.
- **Keep a Changelog 1.1** — human-readable [CHANGELOG.md](../CHANGELOG.md).
- **SLSA L3 build provenance** — every container image is published with signed in-toto SLSA v1.0 provenance via the [`slsa-github-generator` reusable workflow](https://github.com/slsa-framework/slsa-github-generator), co-located in the registry under the image digest.
- **Sigstore / cosign keyless signing** — every published image tag is signed at its immutable digest using GitHub OIDC against the Sigstore Fulcio CA; SBOMs are attached as cosign attestations (`spdxjson` predicate type).
- **CodeQL `security-extended` + `security-and-quality`** — C# and GitHub Actions analyzers run weekly + per-PR.
- **Dependabot** — monthly NuGet, Actions, and Docker base-image updates, grouped to avoid PR noise.
- **Conventional Commits-style messages** — readable history, drives auto-categorized release notes.

## Verifying the supply chain

Every released image is signed by cosign at its immutable digest (keyless OIDC against Sigstore Fulcio), shipped with embedded SLSA L3 build provenance and embedded SBOMs (`docker buildx imagetools inspect ghcr.io/xops-labs/llm-usage-exporter:<tag>` to view), and accompanied by CycloneDX (source) + SPDX (image) SBOMs attached to the GitHub Release.

Verify a tag end-to-end:

```bash
cosign verify ghcr.io/xops-labs/llm-usage-exporter:<tag> \
  --certificate-identity-regexp "https://github.com/xops-labs/llm-usage-exporter/.*" \
  --certificate-oidc-issuer "https://token.actions.githubusercontent.com"
```

See [SECURITY.md → Supply-chain verification](../SECURITY.md#supply-chain-verification) for the full incantation (signature + provenance + SBOM download).

## See also

- [SECURITY.md](../SECURITY.md) — private disclosure policy and supply-chain verification recipes
- [GOVERNANCE.md](../GOVERNANCE.md) — decision-making model
- [docs/deployment.md](deployment.md) — production deployment with `cosign verify` as a deployment gate
