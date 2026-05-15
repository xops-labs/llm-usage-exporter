// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Alerts;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Tests;

public sealed class BudgetEvaluatorTests
{
    [Fact]
    public void Evaluate_ComputesBurnRatioForMatchingBuckets()
    {
        var evaluator = new BudgetEvaluator();
        DateTimeOffset now = new(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
        var budget = new BudgetDefinition
        {
            Name = "global-monthly",
            Period = "monthly",
            LimitUsd = 100m,
        };

        var bucketA = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 2, 0, 0, 0, TimeSpan.Zero),
            "openai",
            "gpt-4o",
            "proj_1",
            12m);

        var bucketB = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
            "anthropic",
            "claude-3",
            "ws_1",
            18m);

        IReadOnlyList<BudgetEvaluation> results = evaluator.Evaluate(
            new[] { bucketA, bucketB },
            new[] { budget },
            now);

        BudgetEvaluation result = Assert.Single(results);
        Assert.Equal("global-monthly", result.Name);
        Assert.Equal(30m, result.Spend);
        Assert.Equal(100m, result.Limit);
        Assert.Equal(0.30, result.BurnRatio, 6);
        Assert.Equal(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), result.PeriodStart);
    }

    [Fact]
    public void Evaluate_RespectsProviderAndModelFilters()
    {
        var evaluator = new BudgetEvaluator();
        DateTimeOffset now = new(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
        var budget = new BudgetDefinition
        {
            Name = "openai-gpt4o",
            Period = "monthly",
            LimitUsd = 200m,
            Providers = new[] { "openai" },
            Models = new[] { "gpt-4o" },
        };

        var matching = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 3, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero),
            "openai",
            "gpt-4o",
            "proj_1",
            40m);

        var wrongProvider = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero),
            "anthropic",
            "claude-3",
            "ws_1",
            500m);

        var wrongModel = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 6, 0, 0, 0, TimeSpan.Zero),
            "openai",
            "gpt-3.5-turbo",
            "proj_1",
            500m);

        IReadOnlyList<BudgetEvaluation> results = evaluator.Evaluate(
            new[] { matching, wrongProvider, wrongModel },
            new[] { budget },
            now);

        BudgetEvaluation result = Assert.Single(results);
        Assert.Equal(40m, result.Spend);
        Assert.Equal(0.20, result.BurnRatio, 6);
    }

    [Fact]
    public void Evaluate_DedupesAcrossInvocations()
    {
        var evaluator = new BudgetEvaluator();
        DateTimeOffset now = new(2026, 5, 12, 12, 0, 0, TimeSpan.Zero);
        var budget = new BudgetDefinition
        {
            Name = "global-monthly",
            Period = "monthly",
            LimitUsd = 100m,
        };

        var bucket = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero),
            "openai",
            "gpt-4o",
            "proj_1",
            25m);

        IReadOnlyList<BudgetEvaluation> first = evaluator.Evaluate(new[] { bucket }, new[] { budget }, now);
        IReadOnlyList<BudgetEvaluation> second = evaluator.Evaluate(new[] { bucket }, new[] { budget }, now);

        Assert.Equal(25m, first[0].Spend);
        Assert.Equal(25m, second[0].Spend);
    }

    [Fact]
    public void Evaluate_ResetsAtPeriodBoundary()
    {
        var evaluator = new BudgetEvaluator();
        var budget = new BudgetDefinition
        {
            Name = "global-monthly",
            Period = "monthly",
            LimitUsd = 100m,
        };

        DateTimeOffset mayNow = new(2026, 5, 28, 12, 0, 0, TimeSpan.Zero);
        var mayBucket = new LlmCostBucket(
            new DateTimeOffset(2026, 5, 20, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 5, 21, 0, 0, 0, TimeSpan.Zero),
            "openai",
            "gpt-4o",
            "proj_1",
            60m);

        IReadOnlyList<BudgetEvaluation> may = evaluator.Evaluate(new[] { mayBucket }, new[] { budget }, mayNow);
        Assert.Equal(60m, may[0].Spend);

        DateTimeOffset juneNow = new(2026, 6, 2, 12, 0, 0, TimeSpan.Zero);
        var juneBucket = new LlmCostBucket(
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 6, 2, 0, 0, 0, TimeSpan.Zero),
            "openai",
            "gpt-4o",
            "proj_1",
            10m);

        IReadOnlyList<BudgetEvaluation> june = evaluator.Evaluate(new[] { juneBucket }, new[] { budget }, juneNow);
        Assert.Equal(10m, june[0].Spend);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), june[0].PeriodStart);
    }
}
