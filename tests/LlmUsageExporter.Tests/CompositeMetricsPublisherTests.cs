// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmUsageExporter.Tests;

public sealed class CompositeMetricsPublisherTests
{
    [Fact]
    public void PublishUsage_ForwardsToEveryInnerPublisher()
    {
        StubMetricsPublisher first = new();
        StubMetricsPublisher second = new();
        CompositeMetricsPublisher composite = new(
            new ILlmMetricsPublisher[] { first, second },
            NullLogger<CompositeMetricsPublisher>.Instance);

        LlmUsageBucket[] usage = new[]
        {
            new LlmUsageBucket(
                DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow,
                "openai",
                "gpt-test",
                "proj_123",
                10,
                5,
                0,
                15,
                3),
        };

        composite.PublishUsage(usage);

        Assert.Equal(1, first.PublishUsageCalls);
        Assert.Equal(1, second.PublishUsageCalls);
        Assert.Same(usage, first.LastUsageBuckets);
        Assert.Same(usage, second.LastUsageBuckets);
    }

    [Fact]
    public void PublishUsage_ContinuesWhenAnInnerPublisherThrows()
    {
        ThrowingMetricsPublisher throwing = new();
        StubMetricsPublisher succeeding = new();
        CompositeMetricsPublisher composite = new(
            new ILlmMetricsPublisher[] { throwing, succeeding },
            NullLogger<CompositeMetricsPublisher>.Instance);

        LlmUsageBucket[] usage = new[]
        {
            new LlmUsageBucket(
                DateTimeOffset.UtcNow.AddHours(-1),
                DateTimeOffset.UtcNow,
                "openai",
                "gpt-test",
                "proj_123",
                10,
                5,
                0,
                15,
                3),
        };

        composite.PublishUsage(usage);

        Assert.Equal(1, throwing.PublishUsageCalls);
        Assert.Equal(1, succeeding.PublishUsageCalls);
    }

    private sealed class StubMetricsPublisher : ILlmMetricsPublisher
    {
        public int PublishUsageCalls { get; private set; }

        public int PublishCostsCalls { get; private set; }

        public IReadOnlyCollection<LlmUsageBucket>? LastUsageBuckets { get; private set; }

        public IReadOnlyCollection<LlmCostBucket>? LastCostBuckets { get; private set; }

        public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
        {
            PublishUsageCalls++;
            LastUsageBuckets = buckets;
        }

        public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
        {
            PublishCostsCalls++;
            LastCostBuckets = buckets;
        }

        public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
        {
        }

        public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
        {
        }
    }

    private sealed class ThrowingMetricsPublisher : ILlmMetricsPublisher
    {
        public int PublishUsageCalls { get; private set; }

        public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
        {
            PublishUsageCalls++;
            throw new InvalidOperationException("boom");
        }

        public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
        {
            throw new InvalidOperationException("boom");
        }

        public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
        {
            throw new InvalidOperationException("boom");
        }

        public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
        {
            throw new InvalidOperationException("boom");
        }
    }
}
