# Azure OpenAI credential setup

The exporter authenticates to Azure with an Azure AD (Microsoft Entra) service principal using the client-credentials flow against `https://management.azure.com/.default`. It only needs two minimum role assignments: **Monitoring Reader** on each Cognitive Services account (for the Azure Monitor metrics REST API) and **Cost Management Reader** on the subscription (for the Cost Management query endpoint). No Cognitive Services data-plane access (e.g. `Cognitive Services User`) is required.

## Least-privilege profile

- Use a dedicated Microsoft Entra app registration and service principal for this exporter and environment.
- Scope `Monitoring Reader` to each `Microsoft.CognitiveServices/accounts/<name>` resource that is scraped. Avoid subscription-wide monitoring roles unless the account list is intentionally subscription-wide.
- Scope `Cost Management Reader` to the narrowest subscription or resource-group scope that still returns the required Azure Cost Management query results.
- Do not grant `Owner`, `Contributor`, `Reader`, `Cognitive Services User`, or model deployment data-plane roles just to run this exporter.
- Store `AZURE_OPENAI_CLIENT_SECRET` in Azure Key Vault, an external secret manager, or a Kubernetes Secret. Rotate it before expiry and restart the exporter after the secret changes.
- The exporter never intentionally logs the client secret, access token, or `Authorization` header. Provider error snippets are redacted before they reach logs, traces, or health state.

## Step 1 — Create an app registration

1. Visit https://entra.microsoft.com → **App registrations** → **New registration**.
2. Name: `llm-usage-exporter`.
3. Supported account types: **Accounts in this organizational directory only**.
4. Redirect URI: leave blank.
5. Click **Register**.

From the new app's **Overview** page, copy:

- **Application (client) ID** → `AZURE_OPENAI_CLIENT_ID`
- **Directory (tenant) ID** → `AZURE_OPENAI_TENANT_ID`

## Step 2 — Create a client secret

1. In the app registration, go to **Certificates & secrets** → **New client secret**.
2. Description: `llm-usage-exporter`. Expiry: 6, 12, or 24 months per your org's policy.
3. Click **Add**.
4. Copy the secret **Value** (not the Secret ID) — Azure only shows it once → `AZURE_OPENAI_CLIENT_SECRET`.
5. Store it in Azure Key Vault or your secrets manager. Do not commit it.

## Step 3 — Grant the two minimum permissions

The service principal needs exactly two role assignments.

### Role 1: Monitoring Reader on each Cognitive Services account

- Action required: `Microsoft.Insights/metrics/read`
- Built-in role: **Monitoring Reader**
- Scope: each `Microsoft.CognitiveServices/accounts/<name>` resource you want to scrape.

In the portal: open the Cognitive Services / Azure OpenAI account → **Access control (IAM)** → **Add** → **Add role assignment** → select **Monitoring Reader** → **Members** → **Select members** → search for `llm-usage-exporter` → **Review + assign**.

Repeat for every account listed in `AZURE_OPENAI_ACCOUNT_RESOURCE_IDS`.

### Role 2: Cost Management Reader on the subscription

- Action required: `Microsoft.CostManagement/query/action`
- Built-in role: **Cost Management Reader** (or a custom role limited to that single action).
- Scope: the subscription that owns the Cognitive Services accounts.

In the portal: **Subscriptions** → select the subscription → **Access control (IAM)** → **Add role assignment** → **Cost Management Reader** → assign to the `llm-usage-exporter` service principal.

### Automation alternative: `az` CLI

```bash
APP_ID=00000000-0000-0000-0000-000000000000           # client id from step 1
SUB_ID=00000000-0000-0000-0000-000000000000           # subscription id
ACCOUNT_ID=/subscriptions/$SUB_ID/resourceGroups/rg-aoai/providers/Microsoft.CognitiveServices/accounts/myaoai

# Materialize the service principal for the app registration (idempotent).
az ad sp create --id $APP_ID

# Role 1: Monitoring Reader on the Cognitive Services account.
az role assignment create \
  --assignee $APP_ID \
  --role "Monitoring Reader" \
  --scope $ACCOUNT_ID

# Role 2: Cost Management Reader on the subscription.
az role assignment create \
  --assignee $APP_ID \
  --role "Cost Management Reader" \
  --scope /subscriptions/$SUB_ID
```

Role assignments can take a few minutes to propagate.

## Step 4 — Collect the resource IDs to scrape

Each Cognitive Services / Azure OpenAI account you want metrics from must be passed by its full ARM resource ID:

```
/subscriptions/<sub-id>/resourceGroups/<rg>/providers/Microsoft.CognitiveServices/accounts/<account-name>
```

Find them via the **Properties** blade of each account in the portal, or list them all with `az`:

```bash
az resource list \
  --resource-type Microsoft.CognitiveServices/accounts \
  --query "[].id" -o tsv
```

Combine them into a comma-separated value for `AZURE_OPENAI_ACCOUNT_RESOURCE_IDS`.

## Step 5 — Configure the exporter

Environment variables:

```bash
AZURE_OPENAI_TENANT_ID=00000000-0000-0000-0000-000000000000
AZURE_OPENAI_CLIENT_ID=00000000-0000-0000-0000-000000000000
AZURE_OPENAI_CLIENT_SECRET=<value from step 2>
AZURE_OPENAI_SUBSCRIPTION_ID=00000000-0000-0000-0000-000000000000
AZURE_OPENAI_ACCOUNT_RESOURCE_IDS=/subscriptions/.../accounts/contoso-aoai,/subscriptions/.../accounts/contoso-aoai-prod
AZURE_OPENAI_RESOURCE_GROUP=rg-aoai        # optional, narrows cost query to this RG
AZURE_OPENAI_MODELS=gpt-4o,gpt-4o-mini     # optional, filter by deployment name
```

Helm `values.yaml` equivalent:

```yaml
azureOpenAI:
  enabled: true
  tenantId: "00000000-0000-0000-0000-000000000000"
  clientId: "00000000-0000-0000-0000-000000000000"
  clientSecretRef:
    name: llm-usage-exporter-azure
    key: client-secret
  subscriptionId: "00000000-0000-0000-0000-000000000000"
  accountResourceIds:
    - /subscriptions/.../accounts/contoso-aoai
    - /subscriptions/.../accounts/contoso-aoai-prod
  resourceGroup: rg-aoai          # optional
  models:                         # optional
    - gpt-4o
    - gpt-4o-mini
```

Mount the client secret from a Kubernetes `Secret` (or external-secrets / Key Vault CSI driver) — do not bake it into the values file.

## Step 6 — Verify

First, confirm the service principal can mint a token:

```bash
TENANT_ID=00000000-0000-0000-0000-000000000000
CLIENT_ID=00000000-0000-0000-0000-000000000000
CLIENT_SECRET='<value from step 2>'

curl -sS -X POST "https://login.microsoftonline.com/$TENANT_ID/oauth2/v2.0/token" \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "client_id=$CLIENT_ID&client_secret=$CLIENT_SECRET&scope=https://management.azure.com/.default&grant_type=client_credentials"
```

A success response looks like:

```json
{
  "token_type": "Bearer",
  "expires_in": 3599,
  "ext_expires_in": 3599,
  "access_token": "eyJ0eXAiOiJKV1Qi..."
}
```

Common AADSTS errors at this step:

- `AADSTS700038` — invalid or wrong-tenant application id.
- `AADSTS7000215` — invalid client secret (expired, mistyped, or copied the Secret ID instead of the Value).
- `AADSTS50034` — the tenant has no such user / service principal. Re-check `AZURE_OPENAI_TENANT_ID`.
- `AADSTS65001` — admin consent required. A directory admin must consent to the app in **API permissions** before the SP can request the scope.

Once the exporter is running, confirm it is polling:

```bash
curl -s http://localhost:8080/metrics | grep 'llm_exporter_poll_success_total{.*provider="azure_openai"'
```

The counter should increase on each successful scrape interval.

## Rotation

Azure AD client secrets have a hard expiry. Rotate without downtime:

1. In the app registration, **Certificates & secrets** → **New client secret**. Two secrets are now valid simultaneously.
2. Update the secret stored in Key Vault / your secrets manager with the new value.
3. Restart the exporter pod (`kubectl rollout restart deploy/llm-usage-exporter`) so it picks up the new secret.
4. Verify the exporter is still polling (Step 6), then delete the old secret in the app registration.

Set a calendar reminder ~30 days before the secret's expiry so rotation never happens under pressure. The expiry date is shown in the **Certificates & secrets** blade.

## Common errors

- `AADSTS700038` — invalid `AZURE_OPENAI_CLIENT_ID`.
- `AADSTS7000215` — invalid `AZURE_OPENAI_CLIENT_SECRET`.
- `AADSTS65001` — admin consent required for the requested scope.
- `403 AuthorizationFailed` — the service principal does not hold the required role at the requested scope. Re-check Step 3.
- `404 ResourceNotFound` — one of the IDs in `AZURE_OPENAI_ACCOUNT_RESOURCE_IDS` is wrong (typo, deleted account, or wrong subscription).

## References

- [Microsoft Entra app registration quickstart](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app)
- [Azure Monitor metrics REST API](https://learn.microsoft.com/en-us/rest/api/monitor/metrics)
- [Cost Management Query API](https://learn.microsoft.com/en-us/rest/api/cost-management/query/usage)
- [Built-in role: Monitoring Reader](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/monitor)
- [Built-in role: Cost Management Reader](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/management-and-governance#cost-management-reader)
