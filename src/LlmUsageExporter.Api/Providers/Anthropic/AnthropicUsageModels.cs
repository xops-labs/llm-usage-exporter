// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.Anthropic;

public sealed class AnthropicUsageResponse
{
    [JsonPropertyName("data")]
    public List<AnthropicUsageBucketDto>? Data { get; init; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; init; }
}

public sealed class AnthropicUsageBucketDto
{
    [JsonPropertyName("starting_at")]
    public string? StartingAt { get; init; }

    [JsonPropertyName("ending_at")]
    public string? EndingAt { get; init; }

    [JsonPropertyName("results")]
    public List<AnthropicUsageResultDto>? Results { get; init; }
}

public sealed class AnthropicUsageResultDto
{
    [JsonPropertyName("uncached_input_tokens")]
    public long? UncachedInputTokens { get; init; }

    [JsonPropertyName("cached_input_tokens")]
    public long? CachedInputTokens { get; init; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public long? CacheCreationInputTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("api_key_id")]
    public string? ApiKeyId { get; init; }
}

public sealed record AnthropicUsageQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string BucketWidth,
    IReadOnlyCollection<string> GroupBy,
    IReadOnlyCollection<string> WorkspaceIds,
    IReadOnlyCollection<string> Models,
    IReadOnlyCollection<string> ApiKeyIds,
    int Limit);
