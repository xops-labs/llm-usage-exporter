// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Tests;

[Collection(MeterListenerCollection.Name)]
public sealed class LlmMeterPublisherTests
{
    [Fact]
    public void PublishUsage_RecordsCounterInstruments()
    {
        using var publisher = new LlmMeterPublisher();
        var (measurements, listener) = SubscribeToPublisher(publisher);
        using (listener)
        {
            var bucket = new LlmUsageBucket(
                DateTimeOffset.FromUnixTimeSeconds(1710000000),
                DateTimeOffset.FromUnixTimeSeconds(1710003600),
                "openai",
                "gpt-test",
                "proj_123",
                10,
                5,
                2,
                15,
                3);

            publisher.PublishUsage([bucket]);

            RecordedMeasurement inputTokens = Assert.Single(measurements, m => m.Name == "llm.usage.input_tokens");
            Assert.Equal(10d, inputTokens.Value);
            Assert.Equal("openai", inputTokens.Tags["provider"]);
            Assert.Equal("gpt-test", inputTokens.Tags["model"]);
            Assert.Equal("proj_123", inputTokens.Tags["tenancy_id"]);

            RecordedMeasurement outputTokens = Assert.Single(measurements, m => m.Name == "llm.usage.output_tokens");
            Assert.Equal(5d, outputTokens.Value);

            RecordedMeasurement totalTokens = Assert.Single(measurements, m => m.Name == "llm.usage.total_tokens");
            Assert.Equal(15d, totalTokens.Value);

            RecordedMeasurement cachedTokens = Assert.Single(measurements, m => m.Name == "llm.usage.cached_input_tokens");
            Assert.Equal(2d, cachedTokens.Value);

            RecordedMeasurement requests = Assert.Single(measurements, m => m.Name == "llm.usage.requests");
            Assert.Equal(3d, requests.Value);
        }
    }

    [Fact]
    public void PublishUsage_DedupesAcrossRepeatedBuckets()
    {
        using var publisher = new LlmMeterPublisher();
        var (measurements, listener) = SubscribeToPublisher(publisher);
        using (listener)
        {
            var bucket = new LlmUsageBucket(
                DateTimeOffset.FromUnixTimeSeconds(1710000000),
                DateTimeOffset.FromUnixTimeSeconds(1710003600),
                "openai",
                "gpt-test",
                "proj_123",
                10,
                5,
                2,
                15,
                3);

            publisher.PublishUsage([bucket]);
            publisher.PublishUsage([bucket]);

            Assert.Single(measurements, m => m.Name == "llm.usage.input_tokens");
            Assert.Single(measurements, m => m.Name == "llm.usage.output_tokens");
            Assert.Single(measurements, m => m.Name == "llm.usage.total_tokens");
            Assert.Single(measurements, m => m.Name == "llm.usage.cached_input_tokens");
            Assert.Single(measurements, m => m.Name == "llm.usage.requests");
        }
    }

    [Fact]
    public void PublishCosts_RecordsCostInstruments()
    {
        using var publisher = new LlmMeterPublisher();
        var (measurements, listener) = SubscribeToPublisher(publisher);
        using (listener)
        {
            var costBucket = new LlmCostBucket(
                DateTimeOffset.FromUnixTimeSeconds(1710000000),
                DateTimeOffset.FromUnixTimeSeconds(1710086400),
                "anthropic",
                "claude-3-sonnet",
                "ws_42",
                2.5m);

            publisher.PublishCosts([costBucket]);

            RecordedMeasurement cost = Assert.Single(measurements, m => m.Name == "llm.usage.cost_usd");
            Assert.Equal(2.5d, cost.Value);
            Assert.Equal("anthropic", cost.Tags["provider"]);
            Assert.Equal("claude-3-sonnet", cost.Tags["model"]);
            Assert.Equal("ws_42", cost.Tags["tenancy_id"]);

            RecordedMeasurement costByModel = Assert.Single(measurements, m => m.Name == "llm.usage.cost_usd_by_model");
            Assert.Equal(2.5d, costByModel.Value);
            Assert.Equal("claude-3-sonnet", costByModel.Tags["model"]);
        }
    }

    // Returns both the measurement bag and the listener so callers can dispose the listener
    // when the test body finishes. Without disposal the listener stays alive on the GC heap
    // and can capture measurements from publishers created in concurrently-running test classes.
    private static (ConcurrentBag<RecordedMeasurement> Measurements, MeterListener Listener) SubscribeToPublisher(LlmMeterPublisher publisher)
    {
        ConcurrentBag<RecordedMeasurement> measurements = new();

        MeterListener listener = new()
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == LlmMeterPublisher.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add(new RecordedMeasurement(instrument.Name, value, ToDictionary(tags)));
        });

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Add(new RecordedMeasurement(instrument.Name, value, ToDictionary(tags)));
        });

        listener.Start();

        _ = publisher;

        return (measurements, listener);
    }

    private static Dictionary<string, string?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        Dictionary<string, string?> result = new(StringComparer.Ordinal);
        for (int index = 0; index < tags.Length; index++)
        {
            KeyValuePair<string, object?> pair = tags[index];
            result[pair.Key] = pair.Value?.ToString();
        }

        return result;
    }

    private sealed record RecordedMeasurement(string Name, double Value, IReadOnlyDictionary<string, string?> Tags);
}
