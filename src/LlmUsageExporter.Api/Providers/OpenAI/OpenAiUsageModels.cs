// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.OpenAI;

public sealed class OpenAiUsageResponse
{
    [JsonPropertyName("data")]
    public List<OpenAiUsageBucketDto>? Data { get; init; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; init; }
}

public sealed class OpenAiUsageBucketDto
{
    [JsonPropertyName("start_time")]
    public long? StartTime { get; init; }

    [JsonPropertyName("end_time")]
    public long? EndTime { get; init; }

    [JsonPropertyName("results")]
    public List<OpenAiUsageResultDto>? Results { get; init; }
}

public sealed class OpenAiUsageResultDto
{
    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("input_cached_tokens")]
    public long? InputCachedTokens { get; init; }

    [JsonPropertyName("num_model_requests")]
    public long? NumModelRequests { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("project_id")]
    public string? ProjectId { get; init; }

    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; init; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; init; }

    [JsonPropertyName("service_tier")]
    public string? ServiceTier { get; init; }
}

public sealed record OpenAiUsageQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string BucketWidth,
    IReadOnlyCollection<string> GroupBy,
    IReadOnlyCollection<string> ProjectIds,
    IReadOnlyCollection<string> Models,
    IReadOnlyCollection<string> ApiKeyIds,
    IReadOnlyCollection<string> UserIds,
    int Limit);
