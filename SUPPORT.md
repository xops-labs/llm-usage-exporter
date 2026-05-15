# Getting support

Thanks for using `llm-usage-exporter`. Picking the right place to ask saves everyone time and gets you an answer faster.

## Where should I go?

| You want to... | Go to |
| --- | --- |
| Ask a question or get help with config | [Discussions → Q&A](../../discussions/categories/q-a) |
| Propose an idea or get roadmap feedback | [Discussions → Ideas](../../discussions/categories/ideas) |
| Show off a dashboard or integration | [Discussions → Show and tell](../../discussions/categories/show-and-tell) |
| Report a reproducible bug | [Open a bug issue](../../issues/new?template=bug_report.yml) |
| Request a feature or new provider | [Open a feature issue](../../issues/new?template=feature_request.yml) |
| Report a security vulnerability **privately** | See [SECURITY.md](SECURITY.md) — do NOT open a public issue |
| Contribute code | See [CONTRIBUTING.md](CONTRIBUTING.md) |
| See who is responsible | See [MAINTAINERS.md](MAINTAINERS.md) |

> **New here?** The Discussions tab is empty by design while the project is young — be the first to post. Q&A threads, idea proposals, and dashboard show-offs are all welcome; the maintainer reads everything and responds on a best-effort basis.

## Before you open an issue

1. **Search** existing issues and discussions — your question may already have an answer.
2. **Check the [README](README.md)** for the metric catalog, env vars, the Docker quick start, and the env-var tables for OTLP, checkpoints, alerts, FOCUS, and multi-tenant mode.
3. **For Kubernetes:** check the [Helm chart README](deploy/helm/llm-usage-exporter/README.md) and `values.yaml` for the most common deployment knobs.
4. **For container-image questions:** verify the signature and pull the SBOM first — see [SECURITY.md → Supply-chain verification](SECURITY.md#supply-chain-verification). A "is this image legit?" question almost always answers itself once you've run `cosign verify`.
5. **Use the templates.** Bug reports without reproduction steps or version info will be closed with a request for more information.

## What "supported" means

This is a community-driven open-source project under an [Apache-2.0 license](LICENSE). Maintainers respond on a best-effort basis. There is **no SLA** for issues or PRs. Commercial support is not offered today; if that changes it will be announced here and in the README.

## Help us help you

- **Redact secrets.** Never paste an `OPENAI_ADMIN_API_KEY`, Azure `clientSecret`, Anthropic admin key, GCP service-account JSON, AWS access keys, OTLP headers, tenant bearer tokens, or any production identifier into an issue, log snippet, or screenshot.
- **Include versions.** The Git SHA or Docker tag of the exporter, your .NET runtime, your Prometheus version, the Helm chart version (if applicable), and your deployment mode (local / Docker Compose / Kubernetes / multi-tenant).
- **Tell us which surfaces are involved.** `/metrics`, `/metrics?tenant=<id>`, `/focus.csv`, `/focus.json`, `/health`, OTLP export, or the alerts pipeline. Each has different debugging paths.
- **Smallest reproducer wins.** A 5-line repro beats a 500-line description.
