// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Providers.Abstractions;
using Prometheus;

namespace LlmUsageExporter.Tests;

/// <summary>
/// Exercises the publisher + checkpoint store contract end-to-end. The publisher
/// uses <see cref="ICheckpointStore.TryRecord"/> to dedupe by
/// <c>(tenant, provider, startTime, endTime, model, tenancyId, metricType)</c>,
/// so re-publishing the same bucket — either because the poll windows overlap
/// or because the process restarted — must NOT advance the counters again.
/// </summary>
public sealed class CheckpointReplayTests : IDisposable
{
    private readonly string _tempRoot;

    public CheckpointReplayTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "llm-checkpoint-replay-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task OverlappingWindows_DoNotDoubleCount_InMemoryStore()
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var store = new InMemoryCheckpointStore();
        var publisher = new LlmMetricsPublisher("openai", factory, store);

        DateTimeOffset bucketStart = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset bucketEnd = DateTimeOffset.FromUnixTimeSeconds(1710003600);

        LlmUsageBucket usage = NewUsageBucket(bucketStart, bucketEnd, "openai", "gpt-replay", "proj_replay", inputTokens: 100, outputTokens: 40);
        LlmCostBucket cost = NewCostBucket(bucketStart, bucketEnd, "openai", "gpt-replay", "proj_replay", costUsd: 2.50m);

        // First poll window: [t, t+1h]
        publisher.PublishUsage([usage]);
        publisher.PublishCosts([cost]);

        // Overlapping poll window: [t-30m, t+30m]. The provider still emits the
        // same hour-aligned bucket, so the identity matches and the publisher
        // must skip it.
        publisher.PublishUsage([usage]);
        publisher.PublishCosts([cost]);

        // And a third pass with the exact same window (idempotent retry).
        publisher.PublishUsage([usage]);
        publisher.PublishCosts([cost]);

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(100, ParseCounter(scrape, "llm_usage_input_tokens_total", "model=\"gpt-replay\""));
        Assert.Equal(40,  ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"gpt-replay\""));
        Assert.Equal(140, ParseCounter(scrape, "llm_usage_total_tokens_total", "model=\"gpt-replay\""));
        Assert.Equal(2.50, ParseCounter(scrape, "llm_usage_cost_usd_total", "tenancy_id=\"proj_replay\""));
    }

    [Fact]
    public async Task NonOverlappingWindows_AreAggregated()
    {
        // Distinct windows (different start/end) yield distinct identities, so
        // the publisher MUST increment for each one. This is the inverse of the
        // overlap test — it guards against an over-zealous dedupe regression.
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var store = new InMemoryCheckpointStore();
        var publisher = new LlmMetricsPublisher("openai", factory, store);

        DateTimeOffset h1Start = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset h1End = DateTimeOffset.FromUnixTimeSeconds(1710003600);
        DateTimeOffset h2Start = h1End;
        DateTimeOffset h2End = DateTimeOffset.FromUnixTimeSeconds(1710007200);

        publisher.PublishUsage([NewUsageBucket(h1Start, h1End, "openai", "gpt-replay", "proj_replay", 100, 40)]);
        publisher.PublishUsage([NewUsageBucket(h2Start, h2End, "openai", "gpt-replay", "proj_replay", 50,  20)]);

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(150, ParseCounter(scrape, "llm_usage_input_tokens_total", "model=\"gpt-replay\""));
        Assert.Equal(60,  ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"gpt-replay\""));
    }

    [Fact]
    public async Task ProcessRestart_FileStoreSurvives_NoDoubleCount()
    {
        string path = Path.Combine(_tempRoot, "replay.jsonl");
        CheckpointStoreOptions options = new()
        {
            Provider = "File",
            FilePath = path,
            RetentionHours = 168,
            MaxEntries = 100,
            FlushIntervalSeconds = 30
        };

        DateTimeOffset bucketStart = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset bucketEnd = DateTimeOffset.FromUnixTimeSeconds(1710003600);

        LlmUsageBucket usage = NewUsageBucket(bucketStart, bucketEnd, "openai", "gpt-restart", "proj_restart", inputTokens: 200, outputTokens: 90);
        LlmCostBucket cost = NewCostBucket(bucketStart, bucketEnd, "openai", "gpt-restart", "proj_restart", costUsd: 4.20m);

        // First "process": publish, flush, dispose.
        {
            CollectorRegistry registry1 = Prometheus.Metrics.NewCustomRegistry();
            IMetricFactory factory1 = Prometheus.Metrics.WithCustomRegistry(registry1);
            var store1 = new FileCheckpointStore(options);
            var publisher1 = new LlmMetricsPublisher("openai", factory1, store1);

            publisher1.PublishUsage([usage]);
            publisher1.PublishCosts([cost]);

            await store1.FlushAsync(CancellationToken.None);
            await store1.DisposeAsync();
        }

        // Sanity: the checkpoint file is on disk and contains records.
        Assert.True(File.Exists(path));
        Assert.NotEmpty(await File.ReadAllLinesAsync(path));

        // Second "process": fresh registry, fresh publisher, fresh store backed
        // by the same file. Replaying the same bucket must NOT increment.
        CollectorRegistry registry2 = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory2 = Prometheus.Metrics.WithCustomRegistry(registry2);
        var store2 = new FileCheckpointStore(options);
        try
        {
            var publisher2 = new LlmMetricsPublisher("openai", factory2, store2);

            publisher2.PublishUsage([usage]);
            publisher2.PublishCosts([cost]);

            string scrape = await ScrapeAsync(registry2);

            // The fresh registry starts at zero. Because the file-backed store
            // remembers every (window,metric) identity from the previous run,
            // PublishUsage/PublishCosts must skip every counter — so the series
            // are never even created. ParseCounter returns 0 when the series is
            // absent, which is the correct semantic here.
            Assert.Equal(0, ParseCounter(scrape, "llm_usage_input_tokens_total", "model=\"gpt-restart\""));
            Assert.Equal(0, ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"gpt-restart\""));
            Assert.Equal(0, ParseCounter(scrape, "llm_usage_total_tokens_total", "model=\"gpt-restart\""));
            Assert.Equal(0, ParseCounter(scrape, "llm_usage_cost_usd_total", "tenancy_id=\"proj_restart\""));
        }
        finally
        {
            await store2.DisposeAsync();
        }
    }

    [Fact]
    public async Task ProcessRestart_NewWindow_IsRecordedNormally()
    {
        // After restart, a *new* window (different timestamps) must still be
        // recorded — restart only suppresses replays of already-seen identities.
        string path = Path.Combine(_tempRoot, "restart-new-window.jsonl");
        CheckpointStoreOptions options = new()
        {
            Provider = "File",
            FilePath = path,
            RetentionHours = 168,
            MaxEntries = 100,
            FlushIntervalSeconds = 30
        };

        DateTimeOffset h1Start = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset h1End = DateTimeOffset.FromUnixTimeSeconds(1710003600);
        DateTimeOffset h2Start = h1End;
        DateTimeOffset h2End = DateTimeOffset.FromUnixTimeSeconds(1710007200);

        {
            CollectorRegistry registry1 = Prometheus.Metrics.NewCustomRegistry();
            IMetricFactory factory1 = Prometheus.Metrics.WithCustomRegistry(registry1);
            var store1 = new FileCheckpointStore(options);
            var publisher1 = new LlmMetricsPublisher("openai", factory1, store1);

            publisher1.PublishUsage([NewUsageBucket(h1Start, h1End, "openai", "gpt-restart-2", "proj_restart_2", 100, 40)]);
            await store1.FlushAsync(CancellationToken.None);
            await store1.DisposeAsync();
        }

        CollectorRegistry registry2 = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory2 = Prometheus.Metrics.WithCustomRegistry(registry2);
        var store2 = new FileCheckpointStore(options);
        try
        {
            var publisher2 = new LlmMetricsPublisher("openai", factory2, store2);

            publisher2.PublishUsage([NewUsageBucket(h1Start, h1End, "openai", "gpt-restart-2", "proj_restart_2", 100, 40)]); // replay
            publisher2.PublishUsage([NewUsageBucket(h2Start, h2End, "openai", "gpt-restart-2", "proj_restart_2", 70,  20)]); // new

            string scrape = await ScrapeAsync(registry2);
            Assert.Equal(70, ParseCounter(scrape, "llm_usage_input_tokens_total", "model=\"gpt-restart-2\""));
            Assert.Equal(20, ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"gpt-restart-2\""));
        }
        finally
        {
            await store2.DisposeAsync();
        }
    }

    [Fact]
    public async Task PerMetricCheckpointing_IsIndependent()
    {
        // The identity includes a metricType slot — so input_tokens and
        // output_tokens for the same window are tracked independently. This
        // matters if a future publisher were to publish costs only on a delayed
        // schedule (cost publish after usage publish should still be recorded).
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var store = new InMemoryCheckpointStore();
        var publisher = new LlmMetricsPublisher("openai", factory, store);

        DateTimeOffset bucketStart = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset bucketEnd = DateTimeOffset.FromUnixTimeSeconds(1710003600);

        publisher.PublishUsage([NewUsageBucket(bucketStart, bucketEnd, "openai", "gpt-multi", "proj_multi", 100, 40)]);
        publisher.PublishCosts([NewCostBucket(bucketStart, bucketEnd, "openai", "gpt-multi", "proj_multi", 1.10m)]);

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(100, ParseCounter(scrape, "llm_usage_input_tokens_total", "model=\"gpt-multi\""));
        Assert.Equal(1.10, ParseCounter(scrape, "llm_usage_cost_usd_total", "tenancy_id=\"proj_multi\""));
    }

    private static LlmUsageBucket NewUsageBucket(
        DateTimeOffset start,
        DateTimeOffset end,
        string provider,
        string model,
        string tenancyId,
        long inputTokens,
        long outputTokens,
        long cachedInputTokens = 0,
        long requestCount = 1)
    {
        return new LlmUsageBucket(
            start,
            end,
            provider,
            model,
            tenancyId,
            inputTokens,
            outputTokens,
            cachedInputTokens,
            inputTokens + outputTokens,
            requestCount);
    }

    private static LlmCostBucket NewCostBucket(
        DateTimeOffset start,
        DateTimeOffset end,
        string provider,
        string model,
        string tenancyId,
        decimal costUsd)
    {
        return new LlmCostBucket(start, end, provider, model, tenancyId, costUsd);
    }

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Returns the numeric value of the first Prometheus sample whose name
    /// matches <paramref name="metricName"/> and whose label block contains the
    /// supplied <paramref name="labelFragment"/>. Returns 0 when no such sample
    /// exists, which matches the absent-series semantic the tests rely on.
    /// </summary>
    private static double ParseCounter(string scrape, string metricName, string labelFragment)
    {
        foreach (string rawLine in scrape.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith(metricName + "{", StringComparison.Ordinal))
            {
                continue;
            }

            if (!line.Contains(labelFragment, StringComparison.Ordinal))
            {
                continue;
            }

            int lastSpace = line.LastIndexOf(' ');
            if (lastSpace < 0 || lastSpace >= line.Length - 1)
            {
                continue;
            }

            string valuePart = line[(lastSpace + 1)..];
            if (double.TryParse(valuePart, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
        }

        return 0;
    }
}
