// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Providers.Abstractions;

/// <summary>
/// Provider-neutral cost bucket. See <see cref="LlmUsageBucket"/> for the
/// <c>TenancyId</c> contract.
/// </summary>
public sealed record LlmCostBucket(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Provider,
    string? Model,
    string? TenancyId,
    decimal CostUsd)
{
    public string Tenant { get; init; } = "default";
}
