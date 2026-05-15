# OpenAI credential setup

The `llm-usage-exporter` polls OpenAI's organization-level Usage and Costs APIs to emit per-model token, request, and dollar metrics. To reach those endpoints it needs an **OpenAI admin API key** scoped to the organization — a normal user API key (`sk-...`) will not work because it cannot read organization usage or cost data. Admin keys grant access only to the admin/reporting surface (`/v1/organizations/usage/*`, `/v1/organizations/costs`); they do not grant data-plane access to chat, completions, or embeddings.

## Least-privilege profile

- Use a dedicated OpenAI organization admin API key named for this exporter and environment, for example `llm-usage-exporter-prod`.
- Do not reuse application inference keys, personal keys, or one shared admin key across dev, staging, and production.
- Use `OPENAI_PROJECT_IDS`, `OPENAI_MODELS`, `OPENAI_API_KEY_IDS`, and `OPENAI_USER_IDS` to limit what the exporter asks for and emits. These filters reduce observability scope; they are not a replacement for protecting the admin key.
- Store `OPENAI_ADMIN_API_KEY` in an external secret manager or Kubernetes Secret. Never put the key in source, container images, Helm values committed to git, dashboards, labels, annotations, or support tickets.
- The exporter never intentionally logs the admin key, `Authorization` header, or `OpenAI-Organization` header. Provider error snippets are redacted before they reach logs, traces, or health state.

## Step 1 — Create an admin API key

1. Visit [https://platform.openai.com/settings/organization/admin-keys](https://platform.openai.com/settings/organization/admin-keys).
2. Sign in as a member of the organization with the `Owner` or `Admin` role. If you only have `Member`, ask an owner to create the key for you.
3. Click **Create admin key**.
4. Name it `llm-usage-exporter` (or `llm-usage-exporter-<env>` if you maintain separate keys per environment).
5. Copy the `sk-admin-...` value immediately — OpenAI shows it exactly once and there is no way to retrieve it later.
6. Store it in your secrets manager (AWS Secrets Manager, GCP Secret Manager, Azure Key Vault, Vault, 1Password, etc.). Never commit it to git, never paste it into chat, and never bake it into a container image.

## Step 2 — Find your organization ID (optional)

If your account belongs to multiple OpenAI organizations, pin the exporter to the right one. Visit [https://platform.openai.com/settings/organization/general](https://platform.openai.com/settings/organization/general) and copy the `Organization ID` value (format: `org_...`). The exporter sends it as the `OpenAI-Organization` HTTP header on every request. If you only have one org, you can omit this — the admin key's default org is used.

## Step 3 — Find your project IDs (optional)

By default the exporter aggregates usage across every project in the org. To narrow the scrape to a subset (for example, only your production projects), visit [https://platform.openai.com/settings/organization/projects](https://platform.openai.com/settings/organization/projects), open each project, and copy its `Project ID` from the URL bar (format: `proj_...`). Supply a comma-separated list to `OPENAI_PROJECT_IDS`.

## Step 4 — Configure the exporter

Production deployments should pass the key via environment variables sourced from a secrets manager:

```bash
OPENAI_ADMIN_API_KEY=sk-admin-xxxxxxxxxxxxxxxxxxxxxxxx
OPENAI_ORG_ID=org_xxxxxxxxxxxxxxxxxxxxxxxx        # optional
OPENAI_PROJECT_IDS=proj_aaaa,proj_bbbb            # optional filter
OPENAI_MODELS=gpt-4o,gpt-4o-mini                  # optional filter
```

The equivalent `appsettings.json` block (use only for local dev — never check in real keys):

```json
{
  "OpenAI": {
    "AdminApiKey": "sk-admin-xxxxxxxxxxxxxxxxxxxxxxxx",
    "OrgId": "org_xxxxxxxxxxxxxxxxxxxxxxxx",
    "ProjectIds": ["proj_aaaa", "proj_bbbb"],
    "Models": ["gpt-4o", "gpt-4o-mini"]
  }
}
```

For the Helm chart, set the matching values in `values.yaml`:

```yaml
openai:
  enabled: true
  adminApiKey: sk-admin-xxxxxxxxxxxxxxxxxxxxxxxx
  orgId: org_xxxxxxxxxxxxxxxxxxxxxxxx
  projectIds:
    - proj_aaaa
    - proj_bbbb
  models:
    - gpt-4o
    - gpt-4o-mini
```

In production, source `adminApiKey` from an external secret (e.g. `existingSecret: llm-usage-exporter-openai`) rather than templating it into `values.yaml` directly.

## Step 5 — Verify

First, sanity-check the credential against the OpenAI Usage API directly:

```bash
START=$(($(date +%s) - 3600))
curl -sS "https://api.openai.com/v1/organizations/usage/completions?start_time=$START" \
  -H "Authorization: Bearer $OPENAI_ADMIN_API_KEY" \
  -H "OpenAI-Organization: $OPENAI_ORG_ID"
```

A healthy response looks like:

```json
{
  "object": "page",
  "data": [
    {
      "object": "bucket",
      "start_time": 1715500800,
      "end_time": 1715504400,
      "results": [
        { "object": "organization.usage.completions.result",
          "input_tokens": 12345, "output_tokens": 6789, "num_model_requests": 42 }
      ]
    }
  ],
  "has_more": false,
  "next_page": null
}
```

Failure modes you may see:

- `{"error":{"code":"invalid_api_key","message":"Incorrect API key provided",...}}` with HTTP 401 — the key is wrong, was revoked, or you copied trailing whitespace.
- `{"error":{"code":"insufficient_permissions",...}}` with HTTP 403 — you used a regular `sk-...` user key instead of an `sk-admin-...` admin key.

Then confirm the exporter itself is polling successfully:

```bash
curl -s http://localhost:8080/health   # should be 200 with status: healthy
curl -s http://localhost:8080/metrics | grep 'llm_exporter_poll_success_total{.*provider="openai"'
```

`llm_exporter_poll_success_total{provider="openai"}` should be non-zero and increasing on each scrape interval. `llm_exporter_poll_failure_total{provider="openai"}` should stay flat.

## Rotation

Admin keys can be rotated with zero downtime:

1. Create a second admin key in the same org (Step 1) named e.g. `llm-usage-exporter-next`.
2. Update the secret in your secrets manager to the new key value.
3. Restart the exporter pod / container — the exporter reads `OPENAI_ADMIN_API_KEY` only at startup and does not hot-reload.
4. After the new key shows successful polls in `/metrics`, revoke the old key from the admin-keys page.

The two keys are valid simultaneously until you revoke the old one, so traffic is never broken.

## Common errors

- `401 invalid_api_key` — wrong, mistyped, or revoked key; trailing newline from a copy-paste; or the key was created in a different org.
- `403 insufficient_permissions` — you supplied a regular user API key (`sk-...`) instead of an admin key (`sk-admin-...`).
- `404 organization not found` — `OPENAI_ORG_ID` does not match the admin key's organization, or the org was deleted.
- `429 rate_limit_exceeded` — the exporter is polling too aggressively; raise `OPENAI_POLL_INTERVAL_SECONDS` (default `60`).

## References

- [OpenAI admin API keys docs](https://platform.openai.com/docs/api-reference/admin-keys)
- [OpenAI Usage API reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/completions)
- [OpenAI Costs API reference](https://developers.openai.com/api/reference/resources/admin/subresources/organization/subresources/usage/methods/costs)
