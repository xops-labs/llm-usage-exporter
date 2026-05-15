# Governance

`llm-usage-exporter` is an open-source project under the [Apache-2.0 license](LICENSE). This document describes how decisions are made today and how that may evolve as the community grows.

> **Status: lightweight.** The project currently has a single maintainer. Heavyweight governance (steering committees, voting, foundations) is intentionally deferred until there are multiple maintainers from multiple organizations. This document will be expanded — not rewritten — when that happens.

---

## Principles

1. **Code first, process second.** Process exists to support contributors, not to gate them.
2. **Transparency by default.** Technical decisions happen in public — in issues, discussions, and PRs.
3. **Lazy consensus.** Silence is consent. If no one objects within the stated window, the change proceeds.
4. **Reversible decisions are cheap.** Ship, learn, revert if needed. Save heavy debate for hard-to-reverse choices like the metric surface and provider model.
5. **Low cardinality, predictable cost.** Metric-shape decisions favor operators who pay the Prometheus bill.

---

## Roles

The full ladder lives in [MAINTAINERS.md](MAINTAINERS.md): **Contributor → Committer → Maintainer → Project lead.** A short version:

- **Contributors** open issues, file PRs, and join discussions. No nomination required.
- **Committers** have triage rights and help with review.
- **Maintainers** can merge to `main`, approve breaking changes, and cut releases.
- **Project lead** is the public point of contact and tie-breaker. Currently the [@xops-labs/maintainers](https://github.com/orgs/xops-labs/teams/maintainers) team.

Maintainer nomination, inactivity, and offboarding are described in [MAINTAINERS.md](MAINTAINERS.md#becoming-a-maintainer).

---

## Decision making

### Day-to-day changes
Bug fixes, doc updates, small features, dependency bumps, and CI tweaks need **one maintainer approval** to merge.

### Larger changes (RFC-lite)
Any of the following require an issue or discussion **opened ≥72 hours before merge** with the `discussion-needed` label:

- Breaking changes to existing metrics, labels, env vars, or endpoints
- Adding or removing a provider (Azure OpenAI, Anthropic, Gemini, Bedrock, ...)
- Adding a label that materially raises Prometheus cardinality
- Changes to release process, license, or governance

The author posts a short proposal — problem, proposal, alternatives, acceptance criteria. Comments accumulate. After 72 hours of silence on blocking concerns, the change can proceed.

We do **not** require a full PEP-style RFC document. If a proposal grows that large, lift it into `docs/rfcs/NNNN-title.md` in the same PR that lands it.

### Disagreements
- Default to talking it out in the issue or PR.
- If maintainers reach an impasse, the **project lead** decides and records the rationale in the thread.
- Conduct-related disputes are not decided here — see [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

---

## Releases

- Versioning follows [Semantic Versioning](https://semver.org/).
- Releases are cut by tagging `vMAJOR.MINOR.PATCH`; the [release workflow](.github/workflows/release.yml) handles image publishing and GitHub Releases.
- The [CHANGELOG.md](CHANGELOG.md) is updated **before** the tag is pushed.
- Security fixes may be released out of cadence.

A predictable cadence will be published in the README once we have enough release history to commit to one.

---

## Conflict of interest

Maintainers employed by vendors of LLM APIs or observability platforms are expected to:

- Disclose the relationship in their GitHub profile or `MAINTAINERS.md` row.
- Recuse themselves from decisions that materially favor their employer over competitors (e.g., choosing default providers, label naming that mirrors a vendor's product).
- Continue to participate normally in all other technical discussions.

---

## Trademark and brand

The name **llm-usage-exporter** and any future logos belong to the project. They may not be used in a way that implies endorsement by the project or by individual maintainers. If trademark filing becomes necessary, the project lead will pursue it on behalf of the project.

---

## Foundation status

The project is **not** currently affiliated with any open-source foundation. Moving to a neutral foundation (CNCF Sandbox, Linux Foundation, etc.) will be considered when:

- There are at least 3 active maintainers from at least 2 unrelated organizations.
- There is a strategic reason (vendor neutrality, broader adoption, joint roadmap with adjacent projects).

A foundation move would be discussed publicly via the RFC-lite process above.

---

## Amending this document

Changes to `GOVERNANCE.md` follow the **larger changes** process: an issue or discussion opened ≥72 hours before the PR is merged, labeled `discussion-needed`. Editorial fixes (typos, broken links, formatting) do not require this.
