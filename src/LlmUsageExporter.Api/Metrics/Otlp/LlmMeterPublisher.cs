// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Metrics.Otlp;

public sealed class LlmMeterPublisher : ILlmMetricsPublisher, IDisposable
{
    public const string MeterName = "LlmUsageExporter";

    private readonly Meter _meter;
    private readonly Counter<long> _inputTokens;
    private readonly Counter<long> _outputTokens;
    private readonly Counter<long> _totalTokens;
    private readonly Counter<long> _cachedInputTokens;
    private readonly Counter<long> _requests;
    private readonly Counter<double> _costUsd;
    private readonly Counter<double> _costUsdByModel;
    private readonly Counter<long> _pollSuccess;
    private readonly Counter<long> _pollFailure;

    private readonly ConcurrentDictionary<string, byte> _seenBuckets = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, long> _lastSuccess = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _lastFailure = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _lastPollDurationSeconds = new(StringComparer.Ordinal);

    private string _activeProvider = "exporter";

    public LlmMeterPublisher()
    {
        _meter = new Meter(MeterName);

        _inputTokens = _meter.CreateCounter<long>("llm.usage.input_tokens");
        _outputTokens = _meter.CreateCounter<long>("llm.usage.output_tokens");
        _totalTokens = _meter.CreateCounter<long>("llm.usage.total_tokens");
        _cachedInputTokens = _meter.CreateCounter<long>("llm.usage.cached_input_tokens");
        _requests = _meter.CreateCounter<long>("llm.usage.requests");
        _costUsd = _meter.CreateCounter<double>(
            "llm.usage.cost_usd",
            unit: "usd",
            description: "Provider-reported operational cost in USD derived from billing history APIs. Reflects spend at the time of the poll; subject to per-provider reporting delays and does not account for credits or discounts finalized after billing closes.");
        _costUsdByModel = _meter.CreateCounter<double>(
            "llm.usage.cost_usd_by_model",
            unit: "usd",
            description: "Provider-reported operational cost in USD per model, derived from billing history APIs. Subject to the same delay and finalization caveats as llm.usage.cost_usd.");
        _pollSuccess = _meter.CreateCounter<long>("llm.exporter.poll_success");
        _pollFailure = _meter.CreateCounter<long>("llm.exporter.poll_failure");

        _meter.CreateObservableGauge(
            "llm.exporter.last_success_timestamp",
            () => _lastSuccess.Select(kvp => new Measurement<long>(kvp.Value, new KeyValuePair<string, object?>("provider", kvp.Key))));

        _meter.CreateObservableGauge(
            "llm.exporter.last_failure_timestamp",
            () => _lastFailure.Select(kvp => new Measurement<long>(kvp.Value, new KeyValuePair<string, object?>("provider", kvp.Key))));

        _meter.CreateObservableGauge(
            "llm.exporter.last_poll_duration_seconds",
            () => _lastPollDurationSeconds.Select(kvp => new Measurement<double>(kvp.Value, new KeyValuePair<string, object?>("provider", kvp.Key))));
    }

    public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
    {
        foreach (LlmUsageBucket bucket in buckets)
        {
            string tenant = ResolveTenant(bucket.Tenant);
            string model = MetricLabelSanitizer.Sanitize(bucket.Model);
            string tenancyId = MetricLabelSanitizer.Sanitize(bucket.TenancyId);
            _activeProvider = bucket.Provider;

            var tags = BuildTags(bucket.Provider, model, tenancyId, tenant);

            EmitCounterOnce(bucket, "input_tokens", bucket.InputTokens, value => _inputTokens.Add(value, tags));
            EmitCounterOnce(bucket, "output_tokens", bucket.OutputTokens, value => _outputTokens.Add(value, tags));
            EmitCounterOnce(bucket, "total_tokens", bucket.TotalTokens, value => _totalTokens.Add(value, tags));
            EmitCounterOnce(bucket, "cached_input_tokens", bucket.CachedInputTokens, value => _cachedInputTokens.Add(value, tags));
            EmitCounterOnce(bucket, "requests", bucket.RequestCount, value => _requests.Add(value, tags));
        }
    }

    public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        foreach (LlmCostBucket bucket in buckets)
        {
            string tenant = ResolveTenant(bucket.Tenant);
            string model = MetricLabelSanitizer.Sanitize(bucket.Model);
            string tenancyId = MetricLabelSanitizer.Sanitize(bucket.TenancyId);
            _activeProvider = bucket.Provider;

            var tags = BuildTags(bucket.Provider, model, tenancyId, tenant);
            double cost = decimal.ToDouble(bucket.CostUsd);

            EmitCostOnce(bucket, "cost_usd", cost, value => _costUsd.Add(value, tags));
            EmitCostOnce(bucket, "cost_usd_by_model", cost, value => _costUsdByModel.Add(value, tags));
        }
    }

    public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
    {
        string provider = _activeProvider;
        _pollSuccess.Add(1, new KeyValuePair<string, object?>("provider", provider));
        _lastSuccess[provider] = timestamp.ToUnixTimeSeconds();
        _lastPollDurationSeconds[provider] = duration.TotalSeconds;
    }

    public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
    {
        string provider = _activeProvider;
        _pollFailure.Add(1, new KeyValuePair<string, object?>("provider", provider));
        _lastFailure[provider] = timestamp.ToUnixTimeSeconds();
        _lastPollDurationSeconds[provider] = duration.TotalSeconds;
    }

    public void Dispose()
    {
        _meter.Dispose();
    }

    private static string ResolveTenant(string bucketTenant)
    {
        return string.IsNullOrWhiteSpace(bucketTenant) ? "default" : bucketTenant;
    }

    private void EmitCounterOnce(LlmUsageBucket bucket, string metricType, long value, Action<long> emit)
    {
        string identity = LlmBucketIdentity.Build(ResolveTenant(bucket.Tenant), bucket.StartTime, bucket.EndTime, bucket.Provider, bucket.Model, bucket.TenancyId, metricType);
        if (!_seenBuckets.TryAdd(identity, 0))
        {
            return;
        }

        if (value > 0)
        {
            emit(value);
        }
    }

    private void EmitCostOnce(LlmCostBucket bucket, string metricType, double value, Action<double> emit)
    {
        string identity = LlmBucketIdentity.Build(ResolveTenant(bucket.Tenant), bucket.StartTime, bucket.EndTime, bucket.Provider, bucket.Model, bucket.TenancyId, metricType);
        if (!_seenBuckets.TryAdd(identity, 0))
        {
            return;
        }

        if (value > 0)
        {
            emit(value);
        }
    }

    private static KeyValuePair<string, object?>[] BuildTags(string provider, string model, string tenancyId, string tenant)
    {
        return new[]
        {
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("tenancy_id", tenancyId),
            new KeyValuePair<string, object?>("tenant", tenant),
        };
    }
}
