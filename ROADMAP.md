# Roadmap

This roadmap describes likely directions for `llm-usage-exporter`. It is not a promise of delivery dates. Priorities
may change based on provider API changes, user feedback, security requirements, and maintainer availability.

## Current Focus

- Stable Prometheus metric shape for LLM usage, requests, tokens, cost, health, budgets, and anomalies
- Provider coverage across OpenAI, Azure OpenAI, Anthropic Claude, Google Gemini, and AWS Bedrock
- Grafana dashboards for usage, cost, provider health, multi-tenant views, and budget signals
- OpenTelemetry OTLP export for teams using OTel collectors and managed observability backends
- FOCUS-compatible cost records for FinOps workflows
- Secure deployment examples for Docker Compose, Kubernetes, Helm, Prometheus, and OTel Collector

## Near-Term

- First tagged release with signed container image and release notes
- Clearer production-readiness checklist for startup and platform teams
- More dashboard examples and PromQL recipes
- More provider API drift tests and sanitized fixtures
- Improved documentation for privacy, data boundaries, and sensitive labels

## Future Ideas

- Additional checkpoint storage backends
- More provider-specific cost freshness documentation
- Optional OpenTelemetry GenAI attribute alignment mode
- FOCUS schema evolution beyond current output
- More deployment examples for managed Kubernetes and Grafana Cloud
- Community-submitted adopter stories, case studies, and dashboard screenshots

## Non-Goals

- Replacing provider invoices or finance-owned billing systems
- Capturing prompt text or model response text
- Acting as a prompt-level observability proxy
- Emitting high-cardinality user-level or API-key-level Prometheus labels by default
