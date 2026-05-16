# Contributing to llm-usage-exporter

Thank you for helping improve practical LLM cost observability.

For larger architectural questions, see [GOVERNANCE.md](GOVERNANCE.md). For the current maintainer list, see [MAINTAINERS.md](MAINTAINERS.md). For how and where to ask questions, see [SUPPORT.md](SUPPORT.md). For private security disclosures, see [SECURITY.md](SECURITY.md) — do **not** open a public issue.

## Table of contents

- [Code of Conduct](#code-of-conduct)
- [Prerequisites](#prerequisites)
- [Getting Started](#getting-started)
- [Project Layout](#project-layout)
- [Git Workflow](#git-workflow)
- [Verify Your Setup](#verify-your-setup)
- [Useful Areas](#useful-areas)
- [Pull Requests](#pull-requests)
- [License](#license)

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you agree to uphold it. See [the Enforcement section](CODE_OF_CONDUCT.md#enforcement) for details.

## Prerequisites

| Requirement | Version / source of truth | Notes |
| --- | --- | --- |
| .NET SDK | `net10.0` from [LlmUsageExporter.Api.csproj](src/LlmUsageExporter.Api/LlmUsageExporter.Api.csproj), pinned for contributors by [global.json](global.json) | CI installs the GA channel — see `dotnet-quality: ga` in [.github/workflows/ci.yml](.github/workflows/ci.yml). |
| Git | Current stable | Required for cloning. |
| Docker (optional) | Current stable with Buildx | Only needed for image builds and the Compose demo at [deploy/docker-compose.yml](deploy/docker-compose.yml). |
| Helm (optional) | `>=3.12` | Only needed for chart changes under [deploy/helm/llm-usage-exporter/](deploy/helm/llm-usage-exporter/). |
| `cosign` (optional) | Current stable | Only needed for verifying release artifacts — see [SECURITY.md → Supply-chain verification](SECURITY.md#supply-chain-verification). |

## Getting Started

```bash
# 1. Fork on GitHub, then clone your fork
git clone git@github.com:YOUR_USERNAME/llm-usage-exporter.git
cd llm-usage-exporter
git remote add upstream git@github.com:xops-labs/llm-usage-exporter.git

# 2. Restore, build, and test
dotnet restore llm-usage-exporter.slnx
dotnet build   llm-usage-exporter.slnx
dotnet test    llm-usage-exporter.slnx

# 3. (Optional) Run the exporter locally and scrape /metrics
cp .env.example .env   # fill in provider credentials before running
dotnet run --project src/LlmUsageExporter.Api
curl http://localhost:8080/metrics | head
```

`.env` is in [.gitignore](.gitignore) by design — copy from [.env.example](.env.example), edit locally, and never commit the result. The same applies to `app/.env.local`, OTLP headers, tenant bearer tokens, provider credentials, and Sigstore signing material.

## Project Layout

```text
llm-usage-exporter/
├── src/LlmUsageExporter.Api/        # ASP.NET Core exporter — providers, metrics, OTLP, FOCUS
├── tests/LlmUsageExporter.Tests/    # xUnit tests
├── dashboards/                      # Grafana dashboard JSON
├── deploy/
│   ├── docker/                      # Docker Compose demo + Prometheus scrape config
│   └── helm/llm-usage-exporter/     # Helm chart
├── docs/                            # User-facing docs (metrics, alerts, FOCUS, multi-tenant)
└── .github/workflows/               # CI, CodeQL, release pipeline
```

Short version: code lives in `src/`, tests in `tests/`, everything operators need to deploy lives in `deploy/` and `dashboards/`, everything they need to understand lives in `docs/`.

## Git Workflow

- Fork [xops-labs/llm-usage-exporter](https://github.com/xops-labs/llm-usage-exporter) and push branches to your fork.
- Pull requests target the upstream `main` branch.
- Keep branches focused and short-lived.

### Branch naming

Use a short, descriptive prefix:

- `fix/anthropic-403-retry`
- `feat/bedrock-batch-job-metrics`
- `docs/helm-chart-quickstart`
- `chore/bump-prometheus-net`

### Starting a branch

```bash
git fetch upstream
git checkout main
git pull --ff-only upstream main
git checkout -b feat/your-change
```

## Verify Your Setup

Before pushing, run the same checks CI runs (matrix: `ubuntu-latest` + `windows-latest` — see [.github/workflows/ci.yml](.github/workflows/ci.yml)):

```bash
dotnet restore llm-usage-exporter.slnx
dotnet build   llm-usage-exporter.slnx --configuration Release --no-restore
dotnet test    llm-usage-exporter.slnx --configuration Release --no-build
```

If you changed the Dockerfile or any deployment artifact:

```bash
docker build -f src/LlmUsageExporter.Api/Dockerfile -t llm-usage-exporter:dev .
```

If you changed the Helm chart:

```bash
helm lint     deploy/helm/llm-usage-exporter
helm template demo deploy/helm/llm-usage-exporter | head -40
```

If you only changed Markdown, the CI `lint-docs` job at [.github/workflows/ci.yml](.github/workflows/ci.yml) is the authoritative check — it verifies the required community files are present.

## Useful Areas

- Additional provider adapters beyond the current five (OpenAI, Azure OpenAI, Anthropic, Gemini, Bedrock).
- Durable checkpoint storage for deduplication.
- Grafana dashboard improvements and new panel ideas.
- Prometheus metric naming and label-cardinality review.
- Security hardening for deployment examples.
- Documentation and reproducible examples.
- Adopter stories, startup usage notes, and production lessons that can improve [ADOPTERS.md](ADOPTERS.md),
  [ROADMAP.md](ROADMAP.md), or the setup docs.

## Updating One Provider Safely

Provider API drift should usually be a contained change. The shared Prometheus metric names, labels, dashboards, alert gauges, OTLP instruments, FOCUS mapper, and polling worker should not change when one upstream provider changes a JSON field, pagination token, auth flow, or cost-report shape.

Use this boundary when updating a provider:

| Layer | Expected change | Avoid changing |
|---|---|---|
| Typed HTTP client under `src/LlmUsageExporter.Api/Providers/<Provider>/` | Request URL, auth headers, retry classification, pagination token handling, response DTOs | `UsagePollingWorker`, shared metric names, dashboard JSON |
| Provider normalizer under `src/LlmUsageExporter.Api/Providers/<Provider>/` | Mapping from provider DTOs into `LlmUsageBucket` and `LlmCostBucket` | The shape of `LlmUsageBucket` / `LlmCostBucket` unless every provider needs it |
| Provider options under `src/LlmUsageExporter.Api/Configuration/` | New provider-specific env vars, validation, defaults | Cross-provider config unless the behavior is genuinely shared |
| Tests under `tests/LlmUsageExporter.Tests/` | Provider-specific mapping tests, fixture tests, retry/pagination tests, golden metrics when output changes intentionally | Existing provider tests unless the shared contract changed |
| Docs under `docs/provider-apis.md` and `docs/credentials/<provider>.md` | Compatibility table, known limitations, credential scopes, upstream reference links | Marketing copy that overstates billing accuracy |

Checklist for provider API drift PRs:

- Link a `Provider API drift` issue or an upstream provider changelog / docs page.
- Add or update sanitized fixtures under `tests/LlmUsageExporter.Tests/Fixtures/provider-payloads/`.
- Add or update golden metric expectations under `tests/LlmUsageExporter.Tests/Fixtures/golden-metrics/` when the emitted metric surface changes.
- Preserve the canonical Prometheus labels: `tenant`, `provider`, `model`, and `tenancy_id`.
- Preserve the near-real-time operational cost signal framing: exported counters are not final billing reconciliation.
- Run `dotnet test llm-usage-exporter.slnx --configuration Release`.

## Pull Requests

1. Push your branch to your fork.
2. Open a pull request against `xops-labs/llm-usage-exporter:main`.
3. Fill in [.github/PULL_REQUEST_TEMPLATE.md](.github/PULL_REQUEST_TEMPLATE.md) completely — every box matters for the reviewer.
4. Link the issue using a closing keyword such as `Closes #123`.
5. Call out any blocked validation commands with the exact command and error.

Adopter entries are welcome. If you use `llm-usage-exporter` in a startup, platform team, internal project, or
open-source project, add a row to [ADOPTERS.md](ADOPTERS.md) with the level of detail you are comfortable sharing.

Larger changes — breaking metric/label changes, new providers, governance or release-process changes — require an issue or discussion **opened ≥72 hours before merge** with the `discussion-needed` label. See [GOVERNANCE.md → Larger changes](GOVERNANCE.md#larger-changes-rfc-lite).

Do not commit:

- API keys, billing-export credentials, or tenant bearer tokens
- `.env`, `.env.local`, or any file containing real provider credentials
- OTLP headers or Sigstore signing material
- Logs that include any of the above

## License

By contributing, you agree your contributions are licensed under [Apache-2.0](LICENSE).
