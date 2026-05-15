// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;
using Prometheus;

namespace LlmUsageExporter.Tests;

/// <summary>
/// Replaces the five per-provider publisher tests that existed before the v1.0
/// unified-namespace refactor. The unified <see cref="LlmMetricsPublisher"/>
/// emits identical metric shape across all providers with <c>provider</c> as a
/// label, so one parameterized test class exercises every provider.
/// </summary>
public sealed class LlmMetricsPublisherTests
{
    public static readonly TheoryData<string, string, string> Providers = new()
    {
        { "openai",       "gpt-test",                          "proj_123" },
        { "azure_openai", "gpt-4o-deploy",                     "/subscriptions/abc/resourceGroups/rg/providers/Microsoft.CognitiveServices/accounts/aoai" },
        { "anthropic",    "claude-3-5-sonnet-20240620",        "wrkspc_abc" },
        { "gemini",       "gemini-1.5-pro",                    "my-gcp-proj" },
        { "bedrock",      "anthropic.claude-3-5-sonnet-v1:0",  "us-east-1" },
    };

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PublishUsage_DoesNotDoubleCountAlreadySeenBuckets(string provider, string model, string tenancyId)
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        var publisher = new LlmMetricsPublisher(provider, Prometheus.Metrics.WithCustomRegistry(registry));
        var bucket = new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            provider,
            model,
            tenancyId,
            10,
            5,
            2,
            15,
            3);

        publisher.PublishUsage([bucket]);
        publisher.PublishUsage([bucket]);

        string metrics = await ScrapeAsync(registry);

        AssertCounter(metrics, "llm_usage_input_tokens_total",  provider, model, tenancyId, 10);
        AssertCounter(metrics, "llm_usage_output_tokens_total", provider, model, tenancyId, 5);
        AssertCounter(metrics, "llm_usage_total_tokens_total",  provider, model, tenancyId, 15);
        Assert.DoesNotContain(BuildSeriesLine("llm_usage_input_tokens_total", provider, model, tenancyId, 20), metrics);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task PublishCosts_DedupesCostBucketsIndependentlyByMetricType(string provider, string model, string tenancyId)
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        var publisher = new LlmMetricsPublisher(provider, Prometheus.Metrics.WithCustomRegistry(registry));
        var bucket = new LlmCostBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710086400),
            provider,
            model,
            tenancyId,
            1.25m);

        publisher.PublishCosts([bucket]);
        publisher.PublishCosts([bucket]);

        string metrics = await ScrapeAsync(registry);

        Assert.Contains($$"""llm_usage_cost_usd_total{tenant="default",provider="{{provider}}",tenancy_id="{{tenancyId}}"} 1.25""", metrics);
        AssertCounter(metrics, "llm_usage_cost_usd_by_model_total", provider, model, tenancyId, 1.25);
        Assert.DoesNotContain($$"""llm_usage_cost_usd_total{tenant="default",provider="{{provider}}",tenancy_id="{{tenancyId}}"} 2.5""", metrics);
    }

    [Fact]
    public async Task PublishUsage_PublishesNewBucketIdentities()
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        var publisher = new LlmMetricsPublisher("openai", Prometheus.Metrics.WithCustomRegistry(registry));
        var firstBucket = new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "openai",
            "gpt-test",
            "proj_123",
            10,
            0,
            0,
            10,
            0);
        var secondBucket = firstBucket with
        {
            StartTime = DateTimeOffset.FromUnixTimeSeconds(1710003600),
            EndTime = DateTimeOffset.FromUnixTimeSeconds(1710007200)
        };

        publisher.PublishUsage([firstBucket, secondBucket]);

        string metrics = await ScrapeAsync(registry);

        AssertCounter(metrics, "llm_usage_input_tokens_total", "openai", "gpt-test", "proj_123", 20);
    }

    [Fact]
    public async Task TwoProviderPublishers_ShareMetricFamiliesAndEmitDistinctProviderLabels()
    {
        // Verify the central design claim: prometheus-net CreateCounter is idempotent
        // on (name, label-names), so two LlmMetricsPublisher instances pointing at the
        // same factory produce a single Counter family with provider as a label
        // dimension — not two parallel families with the same name.
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);

        var openai = new LlmMetricsPublisher("openai", factory);
        var anthropic = new LlmMetricsPublisher("anthropic", factory);

        openai.PublishUsage([new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "openai", "gpt-test", "proj_123",
            100, 50, 0, 150, 5)]);

        anthropic.PublishUsage([new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "anthropic", "claude-test", "wrkspc_abc",
            200, 80, 30, 280, 0)]);

        string metrics = await ScrapeAsync(registry);

        AssertCounter(metrics, "llm_usage_input_tokens_total", "openai",    "gpt-test",    "proj_123",   100);
        AssertCounter(metrics, "llm_usage_input_tokens_total", "anthropic", "claude-test", "wrkspc_abc", 200);

        // Both series share the same metric family — exactly one HELP/TYPE block.
        int helpCount = CountOccurrences(metrics, "# HELP llm_usage_input_tokens_total");
        Assert.Equal(1, helpCount);
    }

    [Fact]
    public void Constructor_RejectsBlankProvider()
    {
        Assert.Throws<ArgumentException>(() => new LlmMetricsPublisher(""));
        Assert.Throws<ArgumentException>(() => new LlmMetricsPublisher("   "));
        Assert.Throws<ArgumentException>(() => new LlmMetricsPublisher(null!));
    }

    private static void AssertCounter(string metrics, string metricName, string provider, string model, string tenancyId, double value)
    {
        string expected = BuildSeriesLine(metricName, provider, model, tenancyId, value);
        Assert.Contains(expected, metrics);
    }

    private static string BuildSeriesLine(string metricName, string provider, string model, string tenancyId, double value)
    {
        string formatted = value % 1 == 0
            ? value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $$"""{{metricName}}{tenant="default",provider="{{provider}}",model="{{model}}",tenancy_id="{{tenancyId}}"} {{formatted}}""";
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
