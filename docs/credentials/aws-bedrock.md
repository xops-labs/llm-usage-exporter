# AWS Bedrock credential setup

The exporter needs an IAM principal with exactly two permissions: `cloudwatch:GetMetricData` and `ce:GetCostAndUsage`. No Bedrock data-plane access (model invocation) is needed — the exporter never calls `InvokeModel`. Every request is signed with AWS SigV4 implemented on top of `System.Security.Cryptography`; no AWS SDK is bundled, so the credential surface is the configured `AWS_*` environment variables.

## Least-privilege profile

- Use a dedicated IAM role or IAM user for this exporter and environment.
- Grant `cloudwatch:GetMetricData` for Bedrock token/request metrics. Add `ce:GetCostAndUsage` only when `BEDROCK_ENABLE_COST_QUERIES=true`.
- Do not grant `AdministratorAccess`, `ReadOnlyAccess`, `bedrock:InvokeModel`, or Bedrock model invocation permissions.
- Prefer short-lived STS credentials from an assumed role, synced into the exporter through an external secret manager or controlled runtime injection. The current exporter does not resolve `AWS_WEB_IDENTITY_TOKEN_FILE` directly because it does not bundle the AWS SDK credential chain.
- Store `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, and optional `AWS_SESSION_TOKEN` in an external secret manager or Kubernetes Secret. Restart the exporter after rotation because credentials are read at startup.
- The exporter never intentionally logs the access key, secret access key, session token, SigV4 `Authorization` header, or `X-Amz-Security-Token`. Provider error snippets are redacted before they reach logs, traces, or health state.

## Step 1 — Define the IAM policy

Two recommended deployment models:

### Option A — Production: IAM role assumed outside the exporter

Recommended for Kubernetes — avoid long-lived static IAM-user keys in the cluster. Create an IAM role with the policy below, then use your platform to assume it and sync short-lived STS credentials into the exporter as `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, and `AWS_SESSION_TOKEN`.

- EKS + IRSA combined with External Secrets Operator, Secrets Store CSI Driver, or another controller that materializes STS credentials for the pod
- Vault AWS secrets engine
- A controlled init/sidecar process that assumes the role and writes a short-lived Kubernetes Secret before exporter startup

### Option B — Local / dev: long-lived IAM user

Create an IAM user with programmatic access and attach the policy below. Generate an access key and secret. Useful for local development but not for production.

Either way, the policy is the same. Save the following as `policy.json` and create a managed policy named `llm-usage-exporter-readonly`:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "BedrockUsageMetrics",
      "Effect": "Allow",
      "Action": "cloudwatch:GetMetricData",
      "Resource": "*"
    },
    {
      "Sid": "BedrockCostExplorer",
      "Effect": "Allow",
      "Action": "ce:GetCostAndUsage",
      "Resource": "*"
    }
  ]
}
```

Notes:

- `cloudwatch:GetMetricData` cannot be scoped by namespace at the IAM level (no resource-level policy). Scope is by account.
- `ce:GetCostAndUsage` likewise has no resource scoping. Filtering to Bedrock is done in the API request body (`Dimensions.SERVICE = "Amazon Bedrock"`), not in IAM.
- Do not attach `AdministratorAccess` or `ReadOnlyAccess` as a shortcut — the policy above is intentionally minimal and audit-friendly.

Create the managed policy once per account:

```bash
aws iam create-policy \
  --policy-name llm-usage-exporter-readonly \
  --policy-document file://policy.json
```

The resulting ARN is `arn:aws:iam::$AWS_ACCOUNT_ID:policy/llm-usage-exporter-readonly`.

## Step 2 — Create the principal

### Option A — IAM role (production / EKS)

```bash
AWS_ACCOUNT_ID=123456789012
OIDC_PROVIDER=oidc.eks.us-east-1.amazonaws.com/id/EXAMPLE0123456789
ROLE_NAME=llm-usage-exporter-readonly
POLICY_ARN=arn:aws:iam::$AWS_ACCOUNT_ID:policy/llm-usage-exporter-readonly

# Trust policy template for EKS IRSA — substitute your OIDC provider URL and SA namespace/name
cat > trust.json <<EOF
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "arn:aws:iam::$AWS_ACCOUNT_ID:oidc-provider/$OIDC_PROVIDER" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {
        "$OIDC_PROVIDER:sub": "system:serviceaccount:observability:llm-usage-exporter",
        "$OIDC_PROVIDER:aud": "sts.amazonaws.com"
      }
    }
  }]
}
EOF

aws iam create-role --role-name $ROLE_NAME --assume-role-policy-document file://trust.json
aws iam attach-role-policy --role-name $ROLE_NAME --policy-arn $POLICY_ARN
```

Then annotate the Kubernetes service account in your Helm values:

```yaml
serviceAccount:
  create: true
  annotations:
    eks.amazonaws.com/role-arn: arn:aws:iam::123456789012:role/llm-usage-exporter-readonly
```

Implementation note: the current exporter does not resolve `AWS_WEB_IDENTITY_TOKEN_FILE`, ECS task-role metadata, or EC2 instance metadata directly. It signs with the values in `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, and optional `AWS_SESSION_TOKEN`.

For ECS or EC2, use your platform's normal secret injection or an init step to expose the assumed-role credentials as the standard `AWS_*` environment variables before the exporter starts.

### Option B — IAM user (local / dev)

```bash
USER_NAME=llm-usage-exporter
POLICY_ARN=arn:aws:iam::$AWS_ACCOUNT_ID:policy/llm-usage-exporter-readonly

aws iam create-user --user-name $USER_NAME
aws iam attach-user-policy --user-name $USER_NAME --policy-arn $POLICY_ARN
aws iam create-access-key --user-name $USER_NAME    # writes AccessKeyId + SecretAccessKey to stdout
```

Store both values in a secrets manager (AWS Secrets Manager, Vault, sealed-secrets, etc.). Never commit them to source.

## Step 3 — Configure the exporter

### For Option A (assumed role / short-lived STS credentials)

Source the short-lived credential triplet from your external secret integration, then set the non-secret Bedrock options:

```bash
AWS_ACCESS_KEY_ID=ASIA...
AWS_SECRET_ACCESS_KEY=...
AWS_SESSION_TOKEN=...
AWS_REGION=us-east-1
BEDROCK_MODEL_IDS=anthropic.claude-3-5-sonnet-20241022-v2:0,amazon.titan-text-express-v1
BEDROCK_ENABLE_COST_QUERIES=true
```

### For Option B (IAM user)

```bash
AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE
AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
AWS_REGION=us-east-1
BEDROCK_MODEL_IDS=anthropic.claude-3-5-sonnet-20241022-v2:0
BEDROCK_ENABLE_COST_QUERIES=true
```

### For STS / role-assumed credentials (transient)

When credentials come from `aws sts assume-role` or an SSO session, three env vars are required:

```bash
AWS_ACCESS_KEY_ID=ASIA...
AWS_SECRET_ACCESS_KEY=...
AWS_SESSION_TOKEN=...
AWS_REGION=us-east-1
```

The signer detects `AWS_SESSION_TOKEN` and adds the `X-Amz-Security-Token` header automatically.

Helm `values.yaml` equivalent for the least-privilege role path. Keep the credential values out of the values file; source them from an external secret manager into the chart-managed Secret or your own Secret.

```yaml
serviceAccount:
  create: true
  annotations:
    eks.amazonaws.com/role-arn: arn:aws:iam::123456789012:role/llm-usage-exporter-readonly
bedrock:
  enabled: true
  region: us-east-1
  modelIds:
    - anthropic.claude-3-5-sonnet-20241022-v2:0
    - amazon.titan-text-express-v1
  enableCostQueries: true
```

Environment variable reference:

| Variable | Required | Default | Notes |
| --- | --- | --- | --- |
| `AWS_ACCESS_KEY_ID` | Option B only | — | Long-lived (`AKIA...`) or STS (`ASIA...`) |
| `AWS_SECRET_ACCESS_KEY` | Option B only | — | Pair with the access key |
| `AWS_SESSION_TOKEN` | STS only | — | Required when `AWS_ACCESS_KEY_ID` is `ASIA...` |
| `AWS_REGION` | no | `us-east-1` | Must match the region where Bedrock metrics are emitted |
| `BEDROCK_MODEL_IDS` | no | (all) | CSV of model IDs to query; empty means no model dimension filter |
| `BEDROCK_ENABLE_COST_QUERIES` | no | `true` | Set `false` to skip `ce:GetCostAndUsage` entirely |

## Step 4 — Verify

Use the AWS CLI under the same principal to confirm both APIs answer:

```bash
aws cloudwatch get-metric-data \
  --metric-data-queries '[{"Id":"m1","MetricStat":{"Metric":{"Namespace":"AWS/Bedrock","MetricName":"InputTokenCount"},"Period":3600,"Stat":"Sum"}}]' \
  --start-time $(date -u -d '1 hour ago' +'%Y-%m-%dT%H:%M:%SZ') \
  --end-time $(date -u +'%Y-%m-%dT%H:%M:%SZ')

aws ce get-cost-and-usage \
  --time-period Start=$(date -u -d 'yesterday' +'%Y-%m-%d'),End=$(date -u +'%Y-%m-%d') \
  --granularity DAILY \
  --metrics BlendedCost \
  --filter '{"Dimensions":{"Key":"SERVICE","Values":["Amazon Bedrock"]}}'
```

A healthy `GetMetricData` response contains a `MetricDataResults` array; if Bedrock has been invoked in the window, `Values` will be non-empty. A healthy `GetCostAndUsage` response contains a `ResultsByTime` array with `Total.BlendedCost.Amount`.

Common errors and what they mean:

- `InvalidClientTokenId` — bad, typo'd, or inactive `AWS_ACCESS_KEY_ID`
- `SignatureDoesNotMatch` — bad `AWS_SECRET_ACCESS_KEY`, or the request region doesn't match `AWS_REGION`
- `AccessDeniedException` — principal is missing `cloudwatch:GetMetricData` or `ce:GetCostAndUsage`
- `ValidationException` — request body shape error; the exporter handles this internally and logs the offending payload

Then confirm the exporter itself is polling successfully:

```bash
curl -s http://localhost:8080/metrics | grep 'llm_exporter_poll_success_total{.*provider="bedrock"'
```

You should see the counter incrementing on each scrape cycle. If it stays at zero, check the exporter logs for `bedrock.poll.failure` entries.

## Rotation

For Option A (assumed role / short-lived STS credentials): rotate the upstream role trust and policy through IAM, and rotate the synced STS credential source according to your external secret controller. The exporter reads credentials at startup, so restart the pod after the synced Secret changes unless your deployment pattern creates a new pod automatically.

For Option B (IAM user) — zero-downtime rotation:

1. Create a new access key for the user (`aws iam create-access-key`). The user now has two valid keys.
2. Update the secret in your secrets manager with the new key pair.
3. Restart the exporter pod / process so it picks up the new env vars.
4. Confirm the new key is being used (CloudTrail → filter by `accessKeyId`).
5. Mark the old key as `Inactive` (`aws iam update-access-key --status Inactive`), wait one full deploy cycle.
6. Delete the old key (`aws iam delete-access-key`).

The exporter does not hot-reload `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` — a restart is required.

## Common errors

- `InvalidClientTokenId` — wrong `AWS_ACCESS_KEY_ID`, or the key is inactive
- `SignatureDoesNotMatch` — wrong `AWS_SECRET_ACCESS_KEY`, or `AWS_REGION` doesn't match the request target
- `AccessDenied` on `cloudwatch:GetMetricData` — IAM policy is missing or not attached to the role/user
- `AccessDenied` on `ce:GetCostAndUsage` — IAM policy is missing OR Cost Explorer is not enabled in the account (enable it once at <https://console.aws.amazon.com/cost-management/home> → Cost Explorer → Activate)
- `ExpiredToken` — `AWS_SESSION_TOKEN` has expired; refresh the assumed-role credential source and restart the exporter pod
- Cost queries silently empty — Cost Explorer data is delayed up to 24h after a service first incurs charges; metrics will populate once AWS finishes the daily roll-up

## References

- [IAM policy creation](https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies_create.html)
- [EKS IRSA](https://docs.aws.amazon.com/eks/latest/userguide/iam-roles-for-service-accounts.html)
- [CloudWatch GetMetricData API](https://docs.aws.amazon.com/AmazonCloudWatch/latest/APIReference/API_GetMetricData.html)
- [Cost Explorer GetCostAndUsage](https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html)
- [Bedrock CloudWatch metrics namespace](https://docs.aws.amazon.com/bedrock/latest/userguide/monitoring-cw.html)
- [AWS SigV4 signing process](https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_aws-signing.html)
- [AWS credential resolution chain](https://docs.aws.amazon.com/sdkref/latest/guide/standardized-credentials.html)
