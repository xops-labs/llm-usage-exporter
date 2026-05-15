// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Alerts;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Tests;

public sealed class AnomalyDetectorTests
{
    [Fact]
    public void Score_IsZeroBeforeMinSamples()
    {
        var detector = new AnomalyDetector();
        var bucket = MakeUsageBucket(totalTokens: 100, hour: 0);

        IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> scores = detector.EvaluateTokens(
            new[] { bucket },
            windowSize: 10,
            minSamples: 6);

        Assert.Equal(0.0, scores[("default", "openai", "gpt-4o")]);
    }

    [Fact]
    public void Score_FlagsLargePositiveDeviation()
    {
        var detector = new AnomalyDetector();

        var history = new List<LlmUsageBucket>();
        for (int i = 0; i < 8; i++)
        {
            history.Add(MakeUsageBucket(totalTokens: 100 + (i % 2 == 0 ? 1 : -1), hour: i));
        }

        detector.EvaluateTokens(history, windowSize: 24, minSamples: 6);

        var spike = MakeUsageBucket(totalTokens: 500, hour: 10);
        IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> scores = detector.EvaluateTokens(
            new[] { spike },
            windowSize: 24,
            minSamples: 6);

        double score = scores[("default", "openai", "gpt-4o")];
        Assert.True(score > 3.0, $"Expected score > 3 but was {score}");
    }

    [Fact]
    public void Score_IsCappedAt10()
    {
        var detector = new AnomalyDetector();

        long[] historyValues = { 100, 101, 99, 100, 102, 98, 101, 100 };
        var history = new List<LlmUsageBucket>();
        for (int i = 0; i < historyValues.Length; i++)
        {
            history.Add(MakeUsageBucket(totalTokens: historyValues[i], hour: i));
        }

        detector.EvaluateTokens(history, windowSize: 24, minSamples: 6);

        var massiveOutlier = MakeUsageBucket(totalTokens: 1_000_000_000, hour: 20);
        IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> scores = detector.EvaluateTokens(
            new[] { massiveOutlier },
            windowSize: 24,
            minSamples: 6);

        double score = scores[("default", "openai", "gpt-4o")];
        Assert.Equal(10.0, score);
    }

    [Fact]
    public void Score_KeysIncludeTenantForMultiTenantHistories()
    {
        var detector = new AnomalyDetector();

        // Two tenants with the same provider/model but different histories.
        var tenantAHistory = new List<LlmUsageBucket>();
        var tenantBHistory = new List<LlmUsageBucket>();
        for (int i = 0; i < 8; i++)
        {
            tenantAHistory.Add(MakeUsageBucket(totalTokens: 100, hour: i, tenant: "alpha"));
            tenantBHistory.Add(MakeUsageBucket(totalTokens: 1000, hour: i, tenant: "beta"));
        }

        detector.EvaluateTokens(tenantAHistory, windowSize: 24, minSamples: 6);
        detector.EvaluateTokens(tenantBHistory, windowSize: 24, minSamples: 6);

        IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> scores = detector.EvaluateTokens(
            Array.Empty<LlmUsageBucket>(),
            windowSize: 24,
            minSamples: 6);

        Assert.Contains(("alpha", "openai", "gpt-4o"), scores.Keys);
        Assert.Contains(("beta", "openai", "gpt-4o"), scores.Keys);
    }

    private static LlmUsageBucket MakeUsageBucket(long totalTokens, int hour, string? tenant = null)
    {
        DateTimeOffset start = new DateTimeOffset(2026, 5, 12, 0, 0, 0, TimeSpan.Zero).AddHours(hour);
        return new LlmUsageBucket(
            start,
            start.AddHours(1),
            "openai",
            "gpt-4o",
            "proj_1",
            InputTokens: totalTokens / 2,
            OutputTokens: totalTokens - (totalTokens / 2),
            CachedInputTokens: 0,
            TotalTokens: totalTokens,
            RequestCount: 1)
        {
            Tenant = tenant ?? "default"
        };
    }
}
