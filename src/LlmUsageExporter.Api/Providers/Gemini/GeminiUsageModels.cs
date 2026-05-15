// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.Gemini;

public sealed class GeminiTimeSeriesResponse
{
    [JsonPropertyName("timeSeries")]
    public List<GeminiTimeSeries>? TimeSeries { get; init; }

    [JsonPropertyName("nextPageToken")]
    public string? NextPageToken { get; init; }
}

public sealed class GeminiTimeSeries
{
    [JsonPropertyName("metric")]
    public GeminiMetricDescriptor? Metric { get; init; }

    [JsonPropertyName("resource")]
    public GeminiMonitoredResource? Resource { get; init; }

    [JsonPropertyName("points")]
    public List<GeminiTimeSeriesPoint>? Points { get; init; }
}

public sealed class GeminiMetricDescriptor
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; init; }
}

public sealed class GeminiMonitoredResource
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("labels")]
    public Dictionary<string, string>? Labels { get; init; }
}

public sealed class GeminiTimeSeriesPoint
{
    [JsonPropertyName("interval")]
    public GeminiTimeSeriesInterval? Interval { get; init; }

    [JsonPropertyName("value")]
    public GeminiPointValue? Value { get; init; }
}

public sealed class GeminiTimeSeriesInterval
{
    [JsonPropertyName("startTime")]
    public DateTimeOffset? StartTime { get; init; }

    [JsonPropertyName("endTime")]
    public DateTimeOffset? EndTime { get; init; }
}

public sealed class GeminiPointValue
{
    [JsonPropertyName("int64Value")]
    public string? Int64Value { get; init; }

    [JsonPropertyName("doubleValue")]
    public double? DoubleValue { get; init; }
}

public sealed record GeminiUsageQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string AlignmentPeriod,
    IReadOnlyCollection<string> Models);
