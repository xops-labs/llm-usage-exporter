// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Focus;

public sealed class FocusObservingPublisher : ILlmMetricsPublisher
{
    private readonly ILlmMetricsPublisher _inner;
    private readonly IFocusRecordStore _store;

    public FocusObservingPublisher(ILlmMetricsPublisher inner, IFocusRecordStore store)
    {
        _inner = inner;
        _store = store;
    }

    public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
    {
        _inner.PublishUsage(buckets);
    }

    public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        _inner.PublishCosts(buckets);

        if (buckets.Count == 0)
        {
            return;
        }

        FocusRecord[] records = FocusRecordMapper.MapCostBuckets(buckets).ToArray();
        if (records.Length > 0)
        {
            _store.Append(records);
        }
    }

    public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
    {
        _inner.RecordPollSuccess(timestamp, duration);
    }

    public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
    {
        _inner.RecordPollFailure(timestamp, duration);
    }
}
