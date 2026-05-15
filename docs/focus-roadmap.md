# FOCUS export — current output and roadmap

## What is FOCUS?

[FOCUS](https://focus.finops.org/focus-specification/) — the FinOps Open Cost & Usage Specification — is an open standard maintained by the FinOps Foundation. It defines a vendor-neutral schema for cloud cost and usage records so FinOps teams can ingest spend from any provider into one normalized dataset without custom ETL per cloud.

`llm-usage-exporter` exposes FOCUS records at `/focus.csv` and `/focus.json`, turning LLM billing data into the same normalized format FinOps tools already understand — alongside AWS Cost and Usage Reports, Azure Cost Management exports, and GCP Billing exports.

---

## Current output — FOCUS 1.0 baseline

The exporter currently emits **FOCUS 1.0** records with one 1.1 column backfilled. Both endpoints signal the schema version via an HTTP response header:

```
X-FOCUS-Version: 1.0
```

### Columns emitted today

| Column | FOCUS version | Value / source |
|---|---|---|
| `BilledCost` | 1.0 | Cost in USD from the provider billing API |
| `BillingAccountId` | 1.0 | Provider-native organization ID (OpenAI org, Azure subscription, etc.) |
| `BillingAccountName` | 1.0 | Provider label or resource name |
| `BillingPeriodStart` | 1.0 | Start of the billing bucket (ISO 8601) |
| `BillingPeriodEnd` | 1.0 | End of the billing bucket (ISO 8601) |
| `ChargePeriodStart` | 1.0 | Same as `BillingPeriodStart` for LLM hourly buckets |
| `ChargePeriodEnd` | 1.0 | Same as `BillingPeriodEnd` |
| `ChargeCategory` | 1.0 | `"Usage"` — all LLM token charges are on-demand usage |
| `ChargeType` | 1.0 | `"Usage"` |
| `ChargeDescription` | 1.0 | Human-readable description including model and bucket window |
| `ConsumedQuantity` | 1.0 | Total tokens consumed in the bucket |
| `ConsumedUnit` | 1.0 | `"tokens"` |
| `CurrencyCode` | 1.0 | `"USD"` |
| `EffectiveCost` | 1.0 | Same as `BilledCost` (no commitment discounts for LLM on-demand) |
| `ListCost` | 1.0 | Same as `BilledCost` (list prices not separately available from billing APIs today) |
| `Provider` | 1.0 | Normalized provider name: `openai`, `azure_openai`, `anthropic`, `gemini`, `bedrock` |
| `Publisher` | 1.0 | Same as `Provider` |
| `ResourceId` | 1.0 | Model identifier |
| `ResourceName` | 1.0 | Model identifier (human-readable alias where available) |
| `ServiceCategory` | 1.0 | `"AI and Machine Learning"` |
| `ServiceName` | 1.0 | Provider service name (e.g., `"Azure OpenAI"`, `"Amazon Bedrock"`) |
| `ServiceSubcategory` | **1.1** | `"Generative AI"` — backfilled for 1.1 compatibility |
| `SubAccountId` | 1.0 | Provider-native project / workspace / account (OpenAI project ID, Anthropic workspace ID, etc.) |
| `SubAccountName` | 1.0 | Normalized `tenancy_id` value |
| `Tags` | 1.0 | `x_provider_native_id` (raw upstream ID) and `x_tenant_id` (exporter tenant label) as extension columns |

### Extension columns

FOCUS allows vendor extension columns prefixed with `x_`. The exporter includes two:

| Column | Value |
|---|---|
| `x_provider_native_id` | The raw provider-native identifier as returned by the upstream API (before label sanitization) |
| `x_tenant_id` | The exporter's logical tenant label for multi-tenant deployments |

---

## Column gap: FOCUS 1.0 → 1.3

FOCUS 1.3 was ratified on December 4, 2025. The table below maps every column added after 1.0 against the current implementation.

| Column | Introduced | Status | Notes |
|---|---|---|---|
| `ServiceSubcategory` | 1.1 | **Emitted** (`"Generative AI"`) | Backfilled despite 1.0 baseline — safe to include early |
| `SkuId` | 1.1 | Planned for 1.3 output | For LLM: the model identifier. Available from provider APIs. |
| `SkuDescription` | 1.1 | Planned for 1.3 output | Human-readable model + service description (e.g., `"OpenAI GPT-4o — output tokens"`). |
| `PricingQuantity` | 1.1 | Deferred | Total token count per bucket. Blocked on exposing token counts through the cost-bucket data model (`LlmCostBucket` currently carries only `CostUsd`, not token counts). |
| `ListUnitPrice` | 1.1 | Deferred | Cost per pricing unit at list price. Blocked on the same data model gap as `PricingQuantity`. Requires per-model list-price tables. |
| `CommitmentDiscount*` columns | 1.1 | Not applicable | LLM token usage is on-demand; commitment discounts (reserved instances, savings plans) do not apply. These columns will remain absent. |
| `ChargeId` | 1.2 | Planned for 1.3 output | Provider-generated unique charge ID. Implementation: a deterministic SHA-256 of `(provider, tenancy_id, model, charge_period_start)` formatted as a URN. |

---

## Roadmap

### FOCUS 1.3 output (planned)

FOCUS 1.3 output will be served from **new versioned endpoints** to preserve backward compatibility for downstream importers that have already set up `CREATE TABLE … AS SELECT` or fixed `COPY` statements against the current schema.

| Endpoint | Schema | Status |
|---|---|---|
| `/focus.csv` | FOCUS 1.0 (current column set) | **Stable — frozen** |
| `/focus.json` | FOCUS 1.0 (current column set) | **Stable — frozen** |
| `/focus/v1.3.csv` | FOCUS 1.3 | Planned — not shipped yet |
| `/focus/v1.3.json` | FOCUS 1.3 | Planned — not shipped yet |

The 1.0 endpoints will not gain new columns. When 1.3 endpoints ship, both versions will be available simultaneously. After a deprecation period (to be announced), the 1.0 endpoints will redirect to a deprecation notice.

**What FOCUS 1.3 adds vs. the current output:**

- `SkuId` — model identifier as a SKU, enabling join with provider pricing catalogs.
- `SkuDescription` — human-readable model and service description.
- `ChargeId` — deterministic unique identifier for each charge row, enabling idempotent import into warehouses.
- `PricingQuantity` + `ListUnitPrice` — deferred until token counts are available in the cost-bucket data model.

### Version-selectable export header

Both existing and future endpoints will include `X-FOCUS-Version` in the response headers so downstream importers can branch on the schema version without parsing the path:

```http
GET /focus.json HTTP/1.1
...

HTTP/1.1 200 OK
Content-Type: application/json
X-FOCUS-Version: 1.0
```

```http
GET /focus/v1.3.json HTTP/1.1
...

HTTP/1.1 200 OK
Content-Type: application/json
X-FOCUS-Version: 1.3
```

### Data model changes required for deferred columns

`PricingQuantity` and `ListUnitPrice` require exposing token counts through the cost-bucket path. Today `LlmCostBucket` carries only `CostUsd`, `Provider`, `Model`, `TenancyId`, `StartTime`, `EndTime`, and `Tenant`. The 1.3 milestone will extend this model to include `InputTokens`, `OutputTokens`, and `TotalTokens` where the provider's cost API returns them alongside the cost figure. Not all providers expose token counts in their billing/cost APIs — for those, `PricingQuantity` will remain absent or zero in the 1.3 output.

---

## Using FOCUS records today

### FinOps tool ingestion

The exporter does not push records to SaaS tools. Treat `/focus.csv` and `/focus.json` as pull surfaces and schedule a small relay in your platform account. Common concrete patterns:

| Tool | Practical ingestion pattern | Exporter endpoint |
|---|---|---|
| Cloudability / IBM Apptio | CronJob or Airflow DAG pulls CSV and writes to an S3 prefix watched by the FOCUS adapter. | `/focus.csv` |
| Vantage | Pull CSV to S3/GCS for a Custom Provider, or POST `/focus.json` through the Vantage API import path. | `/focus.csv` or `/focus.json` |
| ApptioOne / TBM Studio | Pull CSV into the SFTP/Datalink landing directory and apply the FOCUS v1.0 column mapping. | `/focus.csv` |
| OpenCost | Refresh a ConfigMap or PVC file from `/focus.csv`, then point a custom cost source at that mounted file. | `/focus.csv` |
| CloudZero AnyCost | Land CSV/JSON in S3 or proxy `/focus.json` through a tiny authenticated relay endpoint. | `/focus.csv` or `/focus.json` |
| Warehouse-first FinOps | Copy CSV into BigQuery, Snowflake, Redshift, Athena, or Databricks, then union with cloud billing exports. | `/focus.csv` |

Example scheduled S3 landing pattern:

```bash
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.csv?tenant=platform" \
  | aws s3 cp - "s3://acme-finops-focus/llm-usage-exporter/$(date -u +%Y/%m/%d)/focus.csv"
```

Example API relay pattern:

```bash
curl -sf -H "Authorization: Bearer ${TENANT_API_KEY}" \
  "https://llm-metrics.internal.example.com/focus.json?tenant=platform" \
  | curl -sf -X POST "${FINOPS_IMPORT_URL}" \
    -H "Authorization: Bearer ${FINOPS_IMPORT_TOKEN}" \
    -H "Content-Type: application/json" \
    --data-binary @-
```

The recommended pull interval is hourly or daily, depending on the downstream use case. FOCUS records reflect provider billing-history freshness, not second-by-second request traffic. For full per-tool examples, see [docs/standards.md → Importing FOCUS exports into downstream FinOps tools](standards.md#importing-focus-exports-into-downstream-finops-tools).

### Joining with cloud billing exports

Because FOCUS normalizes provider-specific fields, you can union the exporter's output with native cloud billing exports in a single SQL query:

```sql
-- Union LLM cost with infrastructure cost in one FinOps query
SELECT
    Provider,
    ServiceCategory,
    DATE_TRUNC('month', ChargePeriodStart) AS month,
    SUM(BilledCost) AS total_cost_usd
FROM (
    SELECT * FROM cloud_billing_export          -- AWS CUR / Azure Cost Export / GCP Billing
    UNION ALL
    SELECT * FROM llm_focus_import             -- loaded from /focus.csv
)
GROUP BY 1, 2, 3
ORDER BY 4 DESC;
```

The `x_tenant_id` extension column lets you filter or group by the exporter's tenant label — useful for team-level or project-level chargeback without breaking the base FOCUS schema.

---

## See also

- [docs/standards.md](standards.md) — FOCUS compliance table and commitment-discount column exclusions
- [docs/metrics.md → Cost metric semantics](metrics.md#cost-metric-semantics) — currency assumptions and data freshness by provider
- [FOCUS specification](https://focus.finops.org/focus-specification/) — canonical spec reference
