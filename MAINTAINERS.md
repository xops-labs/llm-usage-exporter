# Maintainers

This file lists the people responsible for `llm-usage-exporter`. Maintainers are the only people who can merge to `main`, cut releases, and approve breaking changes.

If you want to report a bug, ask a question, or propose a feature, please use [Issues](../../issues) or [Discussions](../../discussions) instead of contacting maintainers directly.

For private security disclosures, see [SECURITY.md](SECURITY.md).

---

## Current maintainers

| Name | GitHub | Areas |
| --- | --- | --- |
| Yasvanth Udayakumar (creator, lead maintainer) | [LinkedIn](https://www.linkedin.com/in/yasvanth-udayakumar-55298042/) | Project direction · exporter core · all five providers · OTLP · Helm · releases |
| @xops-labs/maintainers | [team page](https://github.com/orgs/xops-labs/teams/maintainers) | Exporter core · all five providers (OpenAI, Azure OpenAI, Anthropic, Gemini, Bedrock) · OTLP + tracing · Helm chart · alerts · FOCUS · multi-tenant · checkpoints · dashboards · releases · supply-chain (SBOM, SLSA, cosign) |

---

## Emeritus maintainers

People who have stepped back from active maintenance but whose past contributions remain part of the project.

_None yet._

---

## Roles

Roles are intentionally lightweight. The formal decision-making model and larger-change process live in [GOVERNANCE.md](GOVERNANCE.md).

### Contributor
Anyone who opens an issue, comments on a discussion, files a PR, improves docs, or writes a dashboard. No nomination required — just show up.

### Committer
A contributor who has had multiple high-quality PRs merged and helps with code review. Committers get the **Triage** role on the repo so they can label, assign, and close issues.

### Maintainer
A committer who is trusted to merge PRs to `main`, approve breaking changes, and cut releases. Maintainers have the **Maintain** role on the repo and are listed in this file and in [.github/CODEOWNERS](.github/CODEOWNERS).

### Project lead
The [@xops-labs/maintainers](https://github.com/orgs/xops-labs/teams/maintainers) team collectively breaks ties when consensus is impossible and is the public point of contact for legal, trademark, and conduct matters.

---

## Becoming a maintainer

There is no fixed quota or schedule. A contributor may be nominated for maintainer status after demonstrating, over a sustained period:

- Multiple substantive PRs merged
- Useful issue triage and review on others' PRs
- Good judgment on scope, backwards compatibility, and metric-shape decisions
- Alignment with the [Code of Conduct](CODE_OF_CONDUCT.md)

**Nomination process:**

1. An existing maintainer opens a private issue (or emails the project lead) proposing the nomination.
2. Existing maintainers have 7 days to raise objections. Silence is consent.
3. If no blocking objections, the nominee is invited. If they accept, they are added to this file and to `CODEOWNERS` in the same PR.

Maintainers may step back at any time. Maintainers who have been inactive for 12+ months without notice may be moved to **Emeritus** by majority of remaining maintainers; commit access is removed but credit is preserved.

---

## Decision making

We use **lazy consensus**:

- For day-to-day PRs: one maintainer review is sufficient to merge.
- For breaking changes, new providers, or changes to the metric surface: open an issue or discussion with the `discussion-needed` label and wait at least **72 hours** for objections before merging.
- If there is sustained disagreement, the project lead decides. Decisions are recorded in the relevant PR or issue.

---

## Release responsibilities

- Cut releases on the cadence published in [README.md](README.md) (or on demand for security fixes).
- Update [CHANGELOG.md](CHANGELOG.md) before tagging.
- Tag follows [Semantic Versioning](https://semver.org/): `vMAJOR.MINOR.PATCH`.
- Push the tag; the release workflow at [.github/workflows/release.yml](.github/workflows/release.yml) handles the rest — multi-arch container build, GHCR publish, cosign keyless signing, SLSA L3 provenance via [`slsa-github-generator`](https://github.com/slsa-framework/slsa-github-generator), CycloneDX + SPDX SBOM emission, and the GitHub Release with SBOMs attached.
- After a release, verify the signature with `cosign verify ghcr.io/xops-labs/llm-usage-exporter:<tag>` (see [SECURITY.md](SECURITY.md#supply-chain-verification) for the full incantation).

---

## Trademark and brand

The name **llm-usage-exporter** and any future logos belong to the project and may not be used to imply endorsement. If the project moves to a foundation or trademark filing, this section will be updated.
