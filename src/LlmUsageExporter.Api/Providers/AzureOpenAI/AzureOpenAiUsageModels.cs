// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.AzureOpenAI;

public sealed class AzureMetricsResponse
{
    [JsonPropertyName("value")]
    public List<AzureMetric>? Value { get; init; }
}

public sealed class AzureMetric
{
    [JsonPropertyName("name")]
    public AzureMetricName? Name { get; init; }

    [JsonPropertyName("unit")]
    public string? Unit { get; init; }

    [JsonPropertyName("timeseries")]
    public List<AzureMetricTimeseries>? Timeseries { get; init; }
}

public sealed class AzureMetricName
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("localizedValue")]
    public string? LocalizedValue { get; init; }
}

public sealed class AzureMetricTimeseries
{
    [JsonPropertyName("metadatavalues")]
    public List<AzureMetricMetadataValue>? MetadataValues { get; init; }

    [JsonPropertyName("data")]
    public List<AzureMetricDatapoint>? Data { get; init; }
}

public sealed class AzureMetricMetadataValue
{
    [JsonPropertyName("name")]
    public AzureMetricName? Name { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

public sealed class AzureMetricDatapoint
{
    [JsonPropertyName("timeStamp")]
    public DateTimeOffset? TimeStamp { get; init; }

    [JsonPropertyName("total")]
    public double? Total { get; init; }

    [JsonPropertyName("average")]
    public double? Average { get; init; }

    [JsonPropertyName("count")]
    public double? Count { get; init; }
}

public sealed class AzureTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; init; }

    [JsonPropertyName("expires_in")]
    public long? ExpiresIn { get; init; }
}

public sealed record AzureOpenAiUsageQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyCollection<string> AccountResourceIds,
    IReadOnlyCollection<string> Models,
    string Interval);
