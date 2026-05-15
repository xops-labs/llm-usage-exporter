# Credential setup runbooks

Step-by-step setup guides for the credentials each LLM provider needs. Every provider in the exporter is independently opt-in — you only need to configure credentials for the providers you actually use.

For shared security controls, rotation patterns, Kubernetes Secret guidance, external secret manager options, and log-redaction expectations, read [Secrets and credential handling](../security-secrets.md).

## Per-provider runbooks

| Provider | What you need | Required permissions | Runbook |
|---|---|---|---|
| **OpenAI** | Admin API key | Organization usage + costs reporting (admin scope) | [openai.md](openai.md) |
| **Azure OpenAI** | Azure AD service principal | `Microsoft.Insights/metrics/read` + `Microsoft.CostManagement/query/action` | [azure-openai.md](azure-openai.md) |
| **Anthropic Claude** | Admin API key | Organization usage + cost reports (admin scope) | [anthropic.md](anthropic.md) |
| **Google Gemini** | GCP service account JSON keyfile | `roles/monitoring.viewer` (+ `roles/bigquery.dataViewer` + `roles/bigquery.jobUser` if cost queries are on) | [gemini.md](gemini.md) |
| **AWS Bedrock** | IAM role (IRSA) or IAM user | `cloudwatch:GetMetricData` + `ce:GetCostAndUsage` | [aws-bedrock.md](aws-bedrock.md) |

## Each runbook covers

1. **Step-by-step UI flow** — clickable screenshots-equivalent walkthrough in the cloud console
2. **CLI alternative** — `gcloud`, `aws`, `az` commands for automation-friendly provisioning
3. **Minimum permissions** — the exact IAM roles / policies, no more
4. **Configuration** — env vars + Helm `values.yaml` snippet
5. **Verification** — a `curl` command to hit the provider's API directly with the credential before pointing the exporter at it
6. **Rotation** — how to rotate without downtime
7. **Common errors** — interpreting the upstream's actual error responses

## After the credential is set up

Wire it into the exporter via one of:

- **Local dev**: append the env vars to `.env` (see [`.env.example`](../../.env.example) for the full set), then `docker compose -f deploy/docker-compose.yml up --build`
- **Kubernetes**: set the provider's block in your Helm `values.yaml` (see [the Helm chart README](../../deploy/helm/llm-usage-exporter/README.md))
- **Multi-tenant deployments**: populate one entry in `Tenants:Items[]` per tenant with that tenant's per-provider credentials — see [docs/configuration.md → Multi-tenant mode](../configuration.md#multi-tenant-mode)

The exporter validates credentials at startup via `.ValidateOnStart()` — misconfigured credentials surface as a clear validation error, not as a silent failure mid-poll.

## Security reminders

- Never commit credential files. The repo's `.gitignore` covers `.env` and `.env.*`, but cloud keyfiles are your responsibility.
- Provider credentials are never intentionally logged by the exporter. Failure snippets are redacted before they reach logs, traces, or health state; do not enable raw HTTP wire logging around the exporter unless your platform redacts headers and form bodies first.
- Treat `/metrics`, `/focus.csv`, and `/focus.json` as operational data — put them behind your normal internal network controls. See [SECURITY.md](../../SECURITY.md) for the full scope.
- For Kubernetes, prefer external secret managers, cloud-native identity, or short-lived credential sync over long-lived credentials committed to Helm values. The provider runbooks call out what the exporter supports directly today.
