// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Metrics;

public interface ILlmMetricsPublisher
{
    void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets);

    void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets);

    void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration);

    void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration);
}
