// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;

namespace LlmUsageExporter.Api.Providers.Abstractions;

public sealed record LlmProviderRegistration(
    string Name,
    ILlmUsageProvider Provider,
    ILlmMetricsPublisher Publisher)
{
    public TenantContext Tenant { get; init; } = TenantContext.Default;
}
