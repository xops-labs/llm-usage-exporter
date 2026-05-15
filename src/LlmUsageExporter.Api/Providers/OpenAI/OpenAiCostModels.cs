// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.OpenAI;

public sealed class OpenAiCostsResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiCostBucketDto>? Data { get; init; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; init; }
}

public sealed class OpenAiCostBucketDto
{
    [JsonPropertyName("start_time")]
    public long? StartTime { get; init; }

    [JsonPropertyName("end_time")]
    public long? EndTime { get; init; }

    [JsonPropertyName("results")]
    public List<OpenAiCostResultDto>? Results { get; init; }
}

public sealed class OpenAiCostResultDto
{
    [JsonPropertyName("amount")]
    public OpenAiCostAmountDto? Amount { get; init; }

    [JsonPropertyName("line_item")]
    public string? LineItem { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; init; }
}

public sealed class OpenAiCostAmountDto
{
    [JsonPropertyName("value")]
    public decimal? Value { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

public sealed record OpenAiCostsQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string BucketWidth,
    IReadOnlyCollection<string> GroupBy,
    IReadOnlyCollection<string> ProjectIds,
    IReadOnlyCollection<string> ApiKeyIds,
    int Limit);
