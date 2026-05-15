// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Alerts;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Tests;

public sealed class AlertObservingRegistrationDecoratorTests
{
    [Fact]
    public void Decorate_WrapsPublisherToForwardToSource()
    {
        var source = new RecordingAlertSource();
        var innerPublisher = new RecordingPublisher();
        var registration = new LlmProviderRegistration(
            "openai",
            new DummyProvider(),
            innerPublisher);

        var decorator = new AlertObservingRegistrationDecorator(source);
        LlmProviderRegistration decorated = decorator.Decorate(registration);

        Assert.IsType<AlertObservingPublisher>(decorated.Publisher);

        var usageBucket = new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "openai",
            "gpt-4o",
            "proj_1",
            10,
            5,
            0,
            15,
            1);

        var costBucket = new LlmCostBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "openai",
            "gpt-4o",
            "proj_1",
            1.50m);

        decorated.Publisher.PublishUsage(new[] { usageBucket });
        decorated.Publisher.PublishCosts(new[] { costBucket });
        decorated.Publisher.RecordPollSuccess(DateTimeOffset.FromUnixTimeSeconds(1710007200), TimeSpan.FromSeconds(2));
        decorated.Publisher.RecordPollFailure(DateTimeOffset.FromUnixTimeSeconds(1710010800), TimeSpan.FromSeconds(3));

        Assert.Equal(1, innerPublisher.UsageCalls);
        Assert.Equal(1, innerPublisher.CostsCalls);
        Assert.Equal(1, innerPublisher.SuccessCalls);
        Assert.Equal(1, innerPublisher.FailureCalls);

        Assert.Single(source.UsageObservations);
        Assert.Single(source.CostObservations);
        Assert.Same(usageBucket, source.UsageObservations[0]);
        Assert.Same(costBucket, source.CostObservations[0]);
    }

    [Fact]
    public void Decorate_IsIdempotent()
    {
        var source = new RecordingAlertSource();
        var registration = new LlmProviderRegistration(
            "openai",
            new DummyProvider(),
            new RecordingPublisher());

        var decorator = new AlertObservingRegistrationDecorator(source);
        LlmProviderRegistration once = decorator.Decorate(registration);
        LlmProviderRegistration twice = decorator.Decorate(once);

        Assert.Same(once.Publisher, twice.Publisher);
    }

    private sealed class RecordingAlertSource : IAlertSource
    {
        public List<LlmUsageBucket> UsageObservations { get; } = new();

        public List<LlmCostBucket> CostObservations { get; } = new();

        public void ObserveUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
        {
            UsageObservations.AddRange(buckets);
        }

        public void ObserveCost(IReadOnlyCollection<LlmCostBucket> buckets)
        {
            CostObservations.AddRange(buckets);
        }

        public IReadOnlyCollection<LlmUsageBucket> DrainUsage()
        {
            LlmUsageBucket[] snapshot = UsageObservations.ToArray();
            UsageObservations.Clear();
            return snapshot;
        }

        public IReadOnlyCollection<LlmCostBucket> DrainCost()
        {
            LlmCostBucket[] snapshot = CostObservations.ToArray();
            CostObservations.Clear();
            return snapshot;
        }
    }

    private sealed class RecordingPublisher : ILlmMetricsPublisher
    {
        public int UsageCalls { get; private set; }

        public int CostsCalls { get; private set; }

        public int SuccessCalls { get; private set; }

        public int FailureCalls { get; private set; }

        public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets) => UsageCalls++;

        public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets) => CostsCalls++;

        public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration) => SuccessCalls++;

        public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration) => FailureCalls++;
    }

    private sealed class DummyProvider : ILlmUsageProvider
    {
        public Task<IReadOnlyCollection<LlmUsageBucket>> GetUsageAsync(
            DateTimeOffset start,
            DateTimeOffset end,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<LlmUsageBucket>>(Array.Empty<LlmUsageBucket>());

        public Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
            DateTimeOffset start,
            DateTimeOffset end,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyCollection<LlmCostBucket>>(Array.Empty<LlmCostBucket>());
    }
}
