// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public sealed class AlertObservingPublisher : ILlmMetricsPublisher
{
    private readonly ILlmMetricsPublisher _inner;
    private readonly IAlertSource _source;

    public AlertObservingPublisher(ILlmMetricsPublisher inner, IAlertSource source)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public ILlmMetricsPublisher Inner => _inner;

    public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
    {
        _inner.PublishUsage(buckets);
        _source.ObserveUsage(buckets);
    }

    public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        _inner.PublishCosts(buckets);
        _source.ObserveCost(buckets);
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
