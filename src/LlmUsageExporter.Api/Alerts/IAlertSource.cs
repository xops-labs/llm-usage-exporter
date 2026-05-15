// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public interface IAlertSource
{
    void ObserveUsage(IReadOnlyCollection<LlmUsageBucket> buckets);

    void ObserveCost(IReadOnlyCollection<LlmCostBucket> buckets);

    IReadOnlyCollection<LlmUsageBucket> DrainUsage();

    IReadOnlyCollection<LlmCostBucket> DrainCost();
}
