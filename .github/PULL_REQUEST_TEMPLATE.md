<!--
Thanks for sending a pull request. A few quick notes before you submit:

1. For non-trivial changes, please open an issue or discussion first so we can align on scope.
2. Keep the PR focused. Smaller PRs ship faster than perfect ones.
3. Run `dotnet build` and `dotnet test` locally before pushing.
-->

## Summary

<!-- 1-3 bullets describing WHAT this PR does and WHY. Link related issues with "Closes #123". -->

-
-

## Type of change

<!-- Tick all that apply. -->

- [ ] Bug fix (non-breaking change that fixes an issue)
- [ ] New feature (non-breaking change that adds functionality)
- [ ] Breaking change (fix or feature that changes existing behavior)
- [ ] Documentation update
- [ ] Internal / refactor / chore (no user-visible change)
- [ ] New provider support (Azure OpenAI, Anthropic, Gemini, Bedrock, ...)
- [ ] New metric, label, or dashboard panel
- [ ] Alerts / FOCUS / OTLP / tracing / multi-tenant / checkpoint change
- [ ] Helm chart change
- [ ] CI / release / supply-chain change (SBOMs, SLSA, cosign)

## Test plan

<!-- How did you verify this works? Be specific. Reviewers should be able to reproduce. -->

- [ ] `dotnet build .\llm-usage-exporter.slnx` succeeds
- [ ] `dotnet test .\llm-usage-exporter.slnx` succeeds
- [ ] Ran exporter locally and scraped `/metrics` (paste a snippet below if metric shape changed)
- [ ] Updated or added unit tests for new behavior
- [ ] Updated README / docs / dashboard JSON if applicable

## Metric / config changes

<!-- If this PR adds, renames, or removes a metric, label, env var, or endpoint, list them here.
Removals and renames are breaking changes for downstream Prometheus queries and dashboards. -->

- [ ] No metric or config surface changed
- [ ] Added new metric(s):
- [ ] Added new env var(s):
- [ ] Renamed or removed existing surface (describe migration path):

## Screenshots / metric snippets

<!-- Optional. For dashboard changes or new metrics, paste a screenshot or scrape output. -->

## Checklist

- [ ] My commit messages follow the project convention (imperative mood, short subject)
- [ ] I have read [CONTRIBUTING.md](../CONTRIBUTING.md)
- [ ] I have read and agree to the [Code of Conduct](../CODE_OF_CONDUCT.md)
- [ ] I have not committed any API keys, secrets, OTLP headers, tenant bearer tokens, or `.env` files
