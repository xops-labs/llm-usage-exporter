// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Prometheus;

namespace LlmUsageExporter.Tests;

/// <summary>
/// Verifies the two isolation planes documented in docs/failure-modes.md:
///
///   1. Per-provider isolation — a failure in one provider's publisher does not
///      stop any other provider's publisher.
///   2. Per-output-plane isolation — an OTLP write failure does not interrupt
///      Prometheus metric accumulation, and vice-versa.
///
/// Also covers the eight failure scenarios in the failure-mode matrix:
///   provider 429, provider 5xx, bad credentials, repeated cursor, OTLP outage,
///   Prometheus scrape failure, checkpoint write failure, and process restart.
///   Process-restart replay is covered in CheckpointReplayTests.cs.
/// </summary>
public sealed class FailureModeTests : IDisposable
{
    private readonly string _tempRoot;

    public FailureModeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "llm-failure-mode-" + Guid.NewGuid().ToString("N"));
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

    // ── Output-plane isolation ────────────────────────────────────────────────

    [Fact]
    public async Task OtlpOutage_PrometheusPublisherContinuesToRecord()
    {
        // Simulates docs/failure-modes.md § "OTLP endpoint down":
        // The OTLP publisher (inner) throws on every call; the Prometheus
        // publisher (inner) must still receive and record the buckets.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var prometheusPublisher = new LlmMetricsPublisher("openai", factory, new InMemoryCheckpointStore());

        // Stand-in for a publisher whose OTLP endpoint is unreachable.
        var otlpOutagePublisher = new AlwaysThrowingPublisher();

        var composite = new CompositeMetricsPublisher(
            [otlpOutagePublisher, prometheusPublisher],
            NullLogger<CompositeMetricsPublisher>.Instance);

        LlmUsageBucket[] usage =
        [
            NewUsageBucket(
                DateTimeOffset.FromUnixTimeSeconds(1710000000),
                DateTimeOffset.FromUnixTimeSeconds(1710003600),
                "openai", "gpt-4o", "proj_otlp_outage",
                inputTokens: 500, outputTokens: 200),
        ];

        composite.PublishUsage(usage);

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(500, ParseCounter(scrape, "llm_usage_input_tokens_total",  "model=\"gpt-4o\""));
        Assert.Equal(200, ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"gpt-4o\""));
    }

    [Fact]
    public async Task PrometheusPublisherThrows_OtlpPublisherContinuesToRecord()
    {
        // Inverse of the above: Prometheus side throws; the OTLP-side stub
        // must still be called. We use a counting stub rather than a real OTLP
        // endpoint — the goal is to prove the composite did not short-circuit.

        var throwingPrometheus = new AlwaysThrowingPublisher();
        var otlpStub = new CountingPublisher();

        var composite = new CompositeMetricsPublisher(
            [throwingPrometheus, otlpStub],
            NullLogger<CompositeMetricsPublisher>.Instance);

        LlmUsageBucket[] usage =
        [
            NewUsageBucket(
                DateTimeOffset.FromUnixTimeSeconds(1710000000),
                DateTimeOffset.FromUnixTimeSeconds(1710003600),
                "openai", "gpt-4o", "proj_prom_fail",
                inputTokens: 100, outputTokens: 40),
        ];

        composite.PublishUsage(usage);

        Assert.Equal(1, otlpStub.PublishUsageCalls);
    }

    // ── Per-provider isolation ────────────────────────────────────────────────

    [Fact]
    public async Task ProviderA_PolFailure_DoesNotAffectProviderB_Metrics()
    {
        // Simulates docs/failure-modes.md § "Provider 429" / "Provider 5xx":
        // Provider A's publisher crashes; Provider B's poll success counter
        // must still increment independently.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);

        var publisherA = new AlwaysThrowingPublisher();    // provider A is down
        var publisherB = new LlmMetricsPublisher("anthropic", factory, new InMemoryCheckpointStore());

        var composite = new CompositeMetricsPublisher(
            [publisherA, publisherB],
            NullLogger<CompositeMetricsPublisher>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        composite.RecordPollFailure(now, TimeSpan.FromSeconds(2));   // A throws, B records
        composite.RecordPollSuccess(now, TimeSpan.FromSeconds(1.5)); // A throws, B records

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(1, ParseCounter(scrape, "llm_exporter_poll_failure_total", "provider=\"anthropic\""));
        Assert.Equal(1, ParseCounter(scrape, "llm_exporter_poll_success_total", "provider=\"anthropic\""));
    }

    // ── Poll counter semantics ────────────────────────────────────────────────

    [Fact]
    public async Task Provider429_RecordsPollFailure_SuccessCounterUnchanged()
    {
        // A 429 response → the worker calls RecordPollFailure, not RecordPollSuccess.
        // The failure counter must increment; the success counter must stay at zero.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var publisher = new LlmMetricsPublisher("openai", factory, new InMemoryCheckpointStore());

        publisher.RecordPollFailure(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1.2));

        string scrape = await ScrapeAsync(registry);
        Assert.True(ParseCounter(scrape, "llm_exporter_poll_failure_total", "provider=\"openai\"") >= 1,
            "Failure counter must be incremented after a 429.");
        Assert.Equal(0, ParseCounter(scrape, "llm_exporter_poll_success_total", "provider=\"openai\""));
    }

    [Fact]
    public async Task Provider5xx_RecordsPollFailure_SuccessCounterUnchanged()
    {
        // 5xx is treated identically to 429 at the publisher level.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var publisher = new LlmMetricsPublisher("gemini", factory, new InMemoryCheckpointStore());

        publisher.RecordPollFailure(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(0.8));
        publisher.RecordPollFailure(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(0.9));

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(2, ParseCounter(scrape, "llm_exporter_poll_failure_total", "provider=\"gemini\""));
        Assert.Equal(0, ParseCounter(scrape, "llm_exporter_poll_success_total", "provider=\"gemini\""));
    }

    [Fact]
    public async Task BadCredentials_RepeatedFailures_SuccessTimestampRemainsPrior()
    {
        // Bad credentials → every poll fails. The last_success_timestamp gauge
        // must stay at its prior value while last_failure_timestamp advances.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var publisher = new LlmMetricsPublisher("azure_openai", factory, new InMemoryCheckpointStore());

        DateTimeOffset priorSuccess = DateTimeOffset.UtcNow.AddHours(-1);
        publisher.RecordPollSuccess(priorSuccess, TimeSpan.FromSeconds(3));

        DateTimeOffset failure1 = priorSuccess.AddMinutes(5);
        DateTimeOffset failure2 = priorSuccess.AddMinutes(10);
        publisher.RecordPollFailure(failure1, TimeSpan.FromSeconds(1));
        publisher.RecordPollFailure(failure2, TimeSpan.FromSeconds(1));

        string scrape = await ScrapeAsync(registry);

        double lastSuccess = ParseGauge(scrape, "llm_exporter_last_success_timestamp", "provider=\"azure_openai\"");
        double lastFailure = ParseGauge(scrape, "llm_exporter_last_failure_timestamp", "provider=\"azure_openai\"");

        Assert.Equal(priorSuccess.ToUnixTimeSeconds(), (long)lastSuccess);
        Assert.Equal(failure2.ToUnixTimeSeconds(), (long)lastFailure);
    }

    // ── Checkpoint write failure ──────────────────────────────────────────────

    [Fact]
    public async Task CheckpointWriteFailure_OtherPublisherInComposite_StillRecords()
    {
        // docs/failure-modes.md § "Checkpoint write failure":
        // If TryRecord throws, PublishUsage propagates the exception.
        // CompositeMetricsPublisher catches it and continues to the next publisher.

        var throwingCheckpointStore = new ThrowingCheckpointStore();

        CollectorRegistry registryA = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factoryA = Prometheus.Metrics.WithCustomRegistry(registryA);
        var failingPublisher = new LlmMetricsPublisher("openai", factoryA, throwingCheckpointStore);

        CollectorRegistry registryB = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factoryB = Prometheus.Metrics.WithCustomRegistry(registryB);
        var healthyPublisher = new LlmMetricsPublisher("openai", factoryB, new InMemoryCheckpointStore());

        var composite = new CompositeMetricsPublisher(
            [failingPublisher, healthyPublisher],
            NullLogger<CompositeMetricsPublisher>.Instance);

        LlmUsageBucket[] usage =
        [
            NewUsageBucket(
                DateTimeOffset.FromUnixTimeSeconds(1710000000),
                DateTimeOffset.FromUnixTimeSeconds(1710003600),
                "openai", "gpt-4o", "proj_checkpoint_fail",
                inputTokens: 300, outputTokens: 120),
        ];

        composite.PublishUsage(usage);

        // The failing publisher's registry should have zero counts (it threw before Inc()).
        string scrapeA = await ScrapeAsync(registryA);
        Assert.Equal(0, ParseCounter(scrapeA, "llm_usage_input_tokens_total", "model=\"gpt-4o\""));

        // The healthy publisher must have recorded normally.
        string scrapeB = await ScrapeAsync(registryB);
        Assert.Equal(300, ParseCounter(scrapeB, "llm_usage_input_tokens_total", "model=\"gpt-4o\""));
    }

    // ── Repeated cursor (checkpoint deduplication) ────────────────────────────

    [Fact]
    public async Task RepeatedCursor_SameWindowReplayedThreeTimes_CountersAdvanceOnce()
    {
        // docs/failure-modes.md § "Repeated cursor":
        // Provider re-emits the same hour-aligned bucket across consecutive polls.
        // The checkpoint identity is stable across replays, so counters must not
        // move after the first publish.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var publisher = new LlmMetricsPublisher("bedrock", factory, new InMemoryCheckpointStore());

        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset end   = DateTimeOffset.FromUnixTimeSeconds(1710003600);

        LlmUsageBucket bucket = NewUsageBucket(start, end, "bedrock", "claude-3-haiku", "us-east-1",
            inputTokens: 800, outputTokens: 300);

        publisher.PublishUsage([bucket]); // first time: new identity → publish
        publisher.PublishUsage([bucket]); // replay 1:  seen identity → skip
        publisher.PublishUsage([bucket]); // replay 2:  seen identity → skip

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(800, ParseCounter(scrape, "llm_usage_input_tokens_total",  "model=\"claude-3-haiku\""));
        Assert.Equal(300, ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"claude-3-haiku\""));
    }

    [Fact]
    public async Task RepeatedCursor_ThenNewWindow_NewWindowIsRecorded()
    {
        // After absorbing replays, the provider eventually emits a genuinely new
        // window. That new window must still be published.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var publisher = new LlmMetricsPublisher("anthropic", factory, new InMemoryCheckpointStore());

        DateTimeOffset h1Start = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset h1End   = DateTimeOffset.FromUnixTimeSeconds(1710003600);
        DateTimeOffset h2Start = h1End;
        DateTimeOffset h2End   = DateTimeOffset.FromUnixTimeSeconds(1710007200);

        LlmUsageBucket h1 = NewUsageBucket(h1Start, h1End, "anthropic", "claude-3-5-sonnet", "ws_abc",
            inputTokens: 400, outputTokens: 150);
        LlmUsageBucket h2 = NewUsageBucket(h2Start, h2End, "anthropic", "claude-3-5-sonnet", "ws_abc",
            inputTokens: 600, outputTokens: 200);

        publisher.PublishUsage([h1]); // new
        publisher.PublishUsage([h1]); // replay — absorbed
        publisher.PublishUsage([h2]); // new window — must be published

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(1000, ParseCounter(scrape, "llm_usage_input_tokens_total",  "model=\"claude-3-5-sonnet\""));
        Assert.Equal(350,  ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"claude-3-5-sonnet\""));
    }

    // ── Prometheus scrape gap ─────────────────────────────────────────────────

    [Fact]
    public async Task PrometheusScrapeMissed_AccumulatedMetricsSurviveToNextScrape()
    {
        // docs/failure-modes.md § "Prometheus scrape failure":
        // Metric values accumulate in the registry between scrapes. A missed
        // scrape does not lose data — the counter is still there on the next scrape.

        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);
        var publisher = new LlmMetricsPublisher("openai", factory, new InMemoryCheckpointStore());

        DateTimeOffset h1Start = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        DateTimeOffset h1End   = DateTimeOffset.FromUnixTimeSeconds(1710003600);
        DateTimeOffset h2Start = h1End;
        DateTimeOffset h2End   = DateTimeOffset.FromUnixTimeSeconds(1710007200);

        publisher.PublishUsage([NewUsageBucket(h1Start, h1End, "openai", "gpt-4o-mini", "proj_gap", 200, 80)]);
        // ← scrape would have fired here but "missed" → no ScrapeAsync call

        publisher.PublishUsage([NewUsageBucket(h2Start, h2End, "openai", "gpt-4o-mini", "proj_gap", 300, 100)]);
        // ← next successful scrape picks up both windows

        string scrape = await ScrapeAsync(registry);
        Assert.Equal(500, ParseCounter(scrape, "llm_usage_input_tokens_total",  "model=\"gpt-4o-mini\""));
        Assert.Equal(180, ParseCounter(scrape, "llm_usage_output_tokens_total", "model=\"gpt-4o-mini\""));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

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

    private static double ParseGauge(string scrape, string metricName, string labelFragment)
        => ParseCounter(scrape, metricName, labelFragment);

    // ── Test doubles ──────────────────────────────────────────────────────────

    /// <summary>Throws on every ILlmMetricsPublisher method — simulates an OTLP endpoint that is unreachable or a Prometheus publisher whose registry is broken.</summary>
    private sealed class AlwaysThrowingPublisher : ILlmMetricsPublisher
    {
        public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
            => throw new InvalidOperationException("Simulated publisher failure (e.g. OTLP endpoint down).");

        public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
            => throw new InvalidOperationException("Simulated publisher failure.");

        public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
            => throw new InvalidOperationException("Simulated publisher failure.");

        public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
            => throw new InvalidOperationException("Simulated publisher failure.");
    }

    /// <summary>Counts calls without doing anything — used to verify a publisher in the composite was reached.</summary>
    private sealed class CountingPublisher : ILlmMetricsPublisher
    {
        public int PublishUsageCalls { get; private set; }
        public int PublishCostsCalls { get; private set; }
        public int RecordSuccessCalls { get; private set; }
        public int RecordFailureCalls { get; private set; }

        public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets) => PublishUsageCalls++;
        public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets) => PublishCostsCalls++;
        public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration) => RecordSuccessCalls++;
        public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration) => RecordFailureCalls++;
    }

    /// <summary>Throws on TryRecord — simulates a disk-full or permission-denied checkpoint store.</summary>
    private sealed class ThrowingCheckpointStore : ICheckpointStore
    {
        public bool TryRecord(string identity)
            => throw new IOException("Simulated checkpoint write failure (disk full or permission denied).");

        public Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
