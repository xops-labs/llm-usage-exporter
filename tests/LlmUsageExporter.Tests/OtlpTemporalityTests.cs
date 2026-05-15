// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Tests;

// Verifies the OTLP temporality contract for llm.usage.* instruments.
//
// The exporter defaults to CUMULATIVE temporality (OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=Cumulative).
// Cumulative requires that all usage instruments are Counter types — counters are monotonically
// increasing and accumulate across the lifetime of the process, which is the observable
// prerequisite that makes cumulative temporality meaningful to downstream backends.
//
// See docs/configuration.md and deploy/otel-collector/README.md#metric-temporality for
// per-backend temporality guidance and the cumulativetodelta collector option.
[Collection(MeterListenerCollection.Name)]
public sealed class OtlpTemporalityTests
{
    [Fact]
    public void UsageInstruments_Are_Counters()
    {
        // Counter<T> instruments are cumulative by definition in the OTel spec —
        // the SDK accumulates additions and the reader reports the running total.
        // Gauge or Histogram instruments would not satisfy the cumulative temporality contract.
        using var publisher = new LlmMeterPublisher();
        var publishedInstruments = new ConcurrentDictionary<string, Instrument>(StringComparer.Ordinal);

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LlmMeterPublisher.MeterName)
            {
                publishedInstruments[instrument.Name] = instrument;
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        // Publish data to trigger counter usage
        publisher.PublishUsage([MakeBucket("openai", "gpt-4o", "proj_a", 0, 3600, 100, 50, 0, 150, 5)]);
        publisher.PublishCosts([MakeCostBucket("openai", "gpt-4o", "proj_a", 0, 3600, 3.14m)]);

        string[] expectedCounterNames =
        [
            "llm.usage.input_tokens",
            "llm.usage.output_tokens",
            "llm.usage.total_tokens",
            "llm.usage.requests",
            "llm.usage.cost_usd",
            "llm.usage.cost_usd_by_model",
        ];

        foreach (string name in expectedCounterNames)
        {
            Assert.True(publishedInstruments.TryGetValue(name, out Instrument? instrument),
                $"Instrument '{name}' was not registered.");
            Assert.True(
                instrument is Counter<long> or Counter<double>,
                $"Instrument '{name}' must be a Counter (cumulative) — got {instrument!.GetType().Name}.");
        }
    }

    [Fact]
    public void HealthInstruments_Are_Counters_Or_ObservableGauges()
    {
        // Poll-success / poll-failure are counters (cumulative).
        // Timestamp and duration instruments are ObservableGauge — they represent
        // instantaneous state, not accumulated totals.
        using var publisher = new LlmMeterPublisher();
        var publishedInstruments = new ConcurrentDictionary<string, Instrument>(StringComparer.Ordinal);

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LlmMeterPublisher.MeterName)
            {
                publishedInstruments[instrument.Name] = instrument;
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        publisher.RecordPollSuccess(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        publisher.RecordPollFailure(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));

        Assert.True(publishedInstruments.TryGetValue("llm.exporter.poll_success", out Instrument? pollSuccess));
        Assert.True(pollSuccess is Counter<long>, "llm.exporter.poll_success must be a Counter.");

        Assert.True(publishedInstruments.TryGetValue("llm.exporter.poll_failure", out Instrument? pollFailure));
        Assert.True(pollFailure is Counter<long>, "llm.exporter.poll_failure must be a Counter.");

        Assert.True(publishedInstruments.TryGetValue("llm.exporter.last_success_timestamp", out Instrument? successTs));
        Assert.True(successTs is ObservableGauge<long>, "llm.exporter.last_success_timestamp must be an ObservableGauge.");

        Assert.True(publishedInstruments.TryGetValue("llm.exporter.last_failure_timestamp", out Instrument? failureTs));
        Assert.True(failureTs is ObservableGauge<long>, "llm.exporter.last_failure_timestamp must be an ObservableGauge.");
    }

    [Fact]
    public void DistinctBuckets_Accumulate_Not_Replace()
    {
        // Cumulative counters accumulate across distinct publishing calls —
        // each Add() increments the running total rather than replacing it.
        // This test verifies that publishing two non-overlapping time buckets
        // results in two separate measurement events, not one overwritten value.
        using var publisher = new LlmMeterPublisher();
        var inputTokenMeasurements = new ConcurrentBag<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LlmMeterPublisher.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "llm.usage.input_tokens")
                inputTokenMeasurements.Add(value);
        });
        listener.Start();

        publisher.PublishUsage([MakeBucket("openai", "gpt-4o", "proj_a", 0, 3600, 100, 50, 0, 150, 5)]);
        publisher.PublishUsage([MakeBucket("openai", "gpt-4o", "proj_a", 3600, 7200, 200, 100, 0, 300, 10)]);

        // Both buckets contribute — counter Add() events are additive, not overwriting
        Assert.Equal(2, inputTokenMeasurements.Count);
        Assert.Equal(300L, inputTokenMeasurements.Sum());
    }

    [Fact]
    public void ZeroValueBuckets_Are_Not_Emitted()
    {
        // Counter Add(0) is a no-op for downstream temporality — the SDK accumulates
        // nothing and the OTLP exporter omits zero-valued data points. This test
        // verifies the exporter suppresses zero-valued measurements rather than
        // polluting the stream with empty increments that could confuse delta consumers.
        using var publisher = new LlmMeterPublisher();
        var measurements = new ConcurrentBag<long>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LlmMeterPublisher.MeterName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "llm.usage.cached_input_tokens")
                measurements.Add(value);
        });
        listener.Start();

        // CachedInputTokens = 0 — should not emit
        publisher.PublishUsage([MakeBucket("openai", "gpt-4o", "proj_a", 0, 3600, 100, 50, 0, 150, 5)]);

        Assert.Empty(measurements);
    }

    private static LlmUsageBucket MakeBucket(
        string provider, string model, string tenancyId,
        long startEpoch, long endEpoch,
        long input, long output, long cached, long total, long requests)
    {
        return new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(startEpoch),
            DateTimeOffset.FromUnixTimeSeconds(endEpoch),
            provider, model, tenancyId,
            input, output, cached, total, requests);
    }

    private static LlmCostBucket MakeCostBucket(
        string provider, string model, string tenancyId,
        long startEpoch, long endEpoch, decimal cost)
    {
        return new LlmCostBucket(
            DateTimeOffset.FromUnixTimeSeconds(startEpoch),
            DateTimeOffset.FromUnixTimeSeconds(endEpoch),
            provider, model, tenancyId, cost);
    }
}
