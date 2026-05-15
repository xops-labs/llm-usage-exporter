# Anthropic credential setup

The exporter needs an Anthropic admin API key to read organization-level usage and cost reports. This is a distinct credential type from regular API keys — admin keys can read the org-level Usage and Cost reports, while regular API keys (the `sk-ant-api-` keys used for inference) cannot. The exporter authenticates with the `x-api-key` header and polls `/v1/organizations/usage_report/messages` and `/v1/organizations/cost_report`.

## Least-privilege profile

- Use a dedicated Anthropic admin API key named for this exporter and environment.
- Do not reuse regular inference API keys, owner personal keys, or one shared admin key across environments.
- Use `ANTHROPIC_WORKSPACE_IDS`, `ANTHROPIC_MODELS`, and `ANTHROPIC_API_KEY_IDS` when a workspace/model subset is enough. These filters reduce emitted telemetry scope; the admin key must still be protected as an organization credential.
- Store `ANTHROPIC_ADMIN_API_KEY` in an external secret manager or Kubernetes Secret. Never put the key in source, container images, Helm values committed to git, dashboards, labels, annotations, or support tickets.
- The exporter never intentionally logs the admin key or `x-api-key` header. Provider error snippets are redacted before they reach logs, traces, or health state.

## Step 1 — Create an admin API key

1. Visit https://console.anthropic.com/settings/admin-keys
2. Sign in as an organization admin (Owner role required)
3. Click "Create Admin Key"
4. Name: `llm-usage-exporter`
5. Copy the key — Anthropic shows it once (starts with `sk-ant-admin-`)
6. Store it in your secrets manager (AWS Secrets Manager, GCP Secret Manager, Vault, etc.)

Note: regular `sk-ant-api-` keys will NOT work — they return `401 authentication_error` against the admin endpoints.

## Step 2 — Find your workspace IDs (optional)

If you want to scope the exporter to specific workspaces, visit https://console.anthropic.com/settings/workspaces and copy each workspace ID (starts with `wrkspc_`). Leave the filter empty to ingest all workspaces in the organization.

## Step 3 — Configure the exporter

Environment variable form:

```bash
ANTHROPIC_ADMIN_API_KEY=sk-ant-admin-xxxxxxxxxxxxxxxxxxxxxxxx
ANTHROPIC_VERSION=2023-06-01                       # rarely needs changing
ANTHROPIC_WORKSPACE_IDS=wrkspc_xxx,wrkspc_yyy      # optional filter
ANTHROPIC_MODELS=claude-3-5-sonnet,claude-3-haiku  # optional filter
ANTHROPIC_API_KEY_IDS=apikey_xxx,apikey_yyy        # optional filter
```

Helm `values.yaml` equivalent:

```yaml
anthropic:
  enabled: true
  adminApiKeySecret:
    name: llm-usage-exporter-anthropic
    key: admin_api_key
  version: "2023-06-01"
  workspaceIds:
    - wrkspc_xxx
    - wrkspc_yyy
  models:
    - claude-3-5-sonnet
    - claude-3-haiku
  apiKeyIds: []
```

## Step 4 — Verify

Test the credential directly against the Anthropic API:

```bash
START_TIME=$(date -u -d '1 hour ago' +'%Y-%m-%dT%H:00:00Z')

curl -sS "https://api.anthropic.com/v1/organizations/usage_report/messages?starting_at=$START_TIME&limit=10" \
  -H "x-api-key: $ANTHROPIC_ADMIN_API_KEY" \
  -H "anthropic-version: 2023-06-01"
```

A successful response returns a `data` array of bucket objects:

```json
{
  "data": [
    {
      "starting_at": "2026-05-12T14:00:00Z",
      "ending_at": "2026-05-12T15:00:00Z",
      "results": [
        {
          "input_tokens": 12345,
          "output_tokens": 6789,
          "cache_read_input_tokens": 0,
          "model": "claude-3-5-sonnet-20241022",
          "workspace_id": "wrkspc_xxx"
        }
      ]
    }
  ],
  "has_more": false
}
```

Error responses:
- `401 authentication_error: invalid x-api-key` — wrong key, typo, revoked key, or a regular `sk-ant-api-` key was used instead of an admin key
- `403 permission_error` — admin key belongs to a different organization than expected

Confirm the exporter is polling successfully:

```bash
curl -s http://localhost:8080/metrics | grep 'llm_exporter_poll_success_total{.*provider="anthropic"'
```

## Rotation

To rotate the admin key without downtime:

1. Create a new admin key in the console (the org now has two valid admin keys)
2. Update the secret in your secrets manager with the new value
3. Restart the exporter pod so it picks up the new env var
4. Revoke the old key in the console after confirming the new one works

The exporter does not hot-reload `ANTHROPIC_ADMIN_API_KEY` — a process restart is required.

## Common errors

- `401 invalid x-api-key` — wrong key, used a regular API key instead of admin, or key was revoked
- `403 permission_error` — admin key belongs to a different organization
- `400 bad_request` — typically a malformed `starting_at` / `ending_at` window; the exporter caps these windows internally, so this is unusual outside of manual curl testing

Important: the Anthropic usage API does NOT return a separate `requests_total` counter — the exporter therefore does not emit `llm_usage_requests_total{provider="anthropic",...}` series. Token counters (`llm_usage_input_tokens_total`, `llm_usage_output_tokens_total`, `llm_usage_cached_input_tokens_total`, `llm_usage_total_tokens_total`) and cost counters (`llm_usage_cost_usd_total`, `llm_usage_cost_usd_by_model_total`) are all emitted normally with `provider="anthropic"`.

## References

- [Anthropic Admin API overview](https://docs.anthropic.com/en/api/administration-api)
- [Usage report endpoint](https://docs.anthropic.com/en/api/admin-api/usage-cost/get-messages-usage-report)
- [Cost report endpoint](https://docs.anthropic.com/en/api/admin-api/usage-cost/get-cost-report)
