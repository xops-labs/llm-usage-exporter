// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Providers.Demo;

namespace LlmUsageExporter.Tests;

public sealed class DemoUsageProviderTests
{
    private static readonly DateTimeOffset WindowEnd = new(2026, 5, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowStart = WindowEnd.AddHours(-1);

    [Fact]
    public async Task GetUsageAsync_EmitsBucketsAcrossModelsAndTenants()
    {
        DemoUsageProvider provider = new();

        IReadOnlyCollection<LlmUsageBucket> buckets = await provider.GetUsageAsync(WindowStart, WindowEnd, CancellationToken.None);

        Assert.NotEmpty(buckets);
        Assert.All(buckets, bucket =>
        {
            Assert.Equal("demo", bucket.Provider);
            Assert.StartsWith("demo-", bucket.Model);
            Assert.StartsWith("demo-team-", bucket.Tenant);
            Assert.NotNull(bucket.TenancyId);
            Assert.True(bucket.InputTokens > 0);
            Assert.True(bucket.OutputTokens > 0);
            Assert.Equal(bucket.InputTokens + bucket.OutputTokens, bucket.TotalTokens);
            Assert.True(bucket.RequestCount >= 1);
        });

        // 12 five-min slots × 5 models × 2 tenants = 120 buckets per hour window.
        Assert.Equal(120, buckets.Count);
        Assert.Equal(5, buckets.Select(b => b.Model).Distinct().Count());
        Assert.Equal(2, buckets.Select(b => b.Tenant).Distinct().Count());
    }

    [Fact]
    public async Task GetCostsAsync_EmitsPositiveCostsPerBucket()
    {
        DemoUsageProvider provider = new();

        IReadOnlyCollection<LlmCostBucket> buckets = await provider.GetCostsAsync(WindowStart, WindowEnd, CancellationToken.None);

        Assert.NotEmpty(buckets);
        Assert.All(buckets, bucket =>
        {
            Assert.Equal("demo", bucket.Provider);
            Assert.True(bucket.CostUsd > 0m);
        });
    }

    [Fact]
    public async Task GetUsageAsync_IsDeterministicAcrossPolls()
    {
        DemoUsageProvider provider = new();

        IReadOnlyCollection<LlmUsageBucket> first = await provider.GetUsageAsync(WindowStart, WindowEnd, CancellationToken.None);
        IReadOnlyCollection<LlmUsageBucket> second = await provider.GetUsageAsync(WindowStart, WindowEnd, CancellationToken.None);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task GetUsageAsync_AlignsBucketsToFiveMinuteWallClock()
    {
        DemoUsageProvider provider = new();

        IReadOnlyCollection<LlmUsageBucket> buckets = await provider.GetUsageAsync(WindowStart, WindowEnd, CancellationToken.None);

        Assert.All(buckets, bucket =>
        {
            Assert.Equal(0, bucket.StartTime.Minute % 5);
            Assert.Equal(0, bucket.StartTime.Second);
            Assert.Equal(TimeSpan.FromMinutes(5), bucket.EndTime - bucket.StartTime);
        });
    }
}
