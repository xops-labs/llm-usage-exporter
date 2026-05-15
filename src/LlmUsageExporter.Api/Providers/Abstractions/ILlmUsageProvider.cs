// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Providers.Abstractions;

public interface ILlmUsageProvider
{
    Task<IReadOnlyCollection<LlmUsageBucket>> GetUsageAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken);

    Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken);
}
