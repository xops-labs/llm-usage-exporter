// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Metrics;

public sealed class CompositeMetricsPublisher : ILlmMetricsPublisher
{
    private readonly IReadOnlyList<ILlmMetricsPublisher> _publishers;
    private readonly ILogger<CompositeMetricsPublisher> _logger;

    public CompositeMetricsPublisher(
        IEnumerable<ILlmMetricsPublisher> publishers,
        ILogger<CompositeMetricsPublisher> logger)
    {
        _publishers = publishers.ToArray();
        _logger = logger;
    }

    public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
    {
        foreach (ILlmMetricsPublisher publisher in _publishers)
        {
            try
            {
                publisher.PublishUsage(buckets);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Inner publisher {Publisher} threw during PublishUsage; continuing with remaining publishers.",
                    publisher.GetType().Name);
            }
        }
    }

    public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        foreach (ILlmMetricsPublisher publisher in _publishers)
        {
            try
            {
                publisher.PublishCosts(buckets);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Inner publisher {Publisher} threw during PublishCosts; continuing with remaining publishers.",
                    publisher.GetType().Name);
            }
        }
    }

    public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
    {
        foreach (ILlmMetricsPublisher publisher in _publishers)
        {
            try
            {
                publisher.RecordPollSuccess(timestamp, duration);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Inner publisher {Publisher} threw during RecordPollSuccess; continuing with remaining publishers.",
                    publisher.GetType().Name);
            }
        }
    }

    public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
    {
        foreach (ILlmMetricsPublisher publisher in _publishers)
        {
            try
            {
                publisher.RecordPollFailure(timestamp, duration);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Inner publisher {Publisher} threw during RecordPollFailure; continuing with remaining publishers.",
                    publisher.GetType().Name);
            }
        }
    }
}
