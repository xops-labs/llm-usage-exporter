// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.Anthropic;

public sealed class AnthropicCostResponse
{
    [JsonPropertyName("data")]
    public List<AnthropicCostBucketDto>? Data { get; init; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; init; }

    [JsonPropertyName("next_page")]
    public string? NextPage { get; init; }
}

public sealed class AnthropicCostBucketDto
{
    [JsonPropertyName("starting_at")]
    public string? StartingAt { get; init; }

    [JsonPropertyName("ending_at")]
    public string? EndingAt { get; init; }

    [JsonPropertyName("results")]
    public List<AnthropicCostResultDto>? Results { get; init; }
}

public sealed class AnthropicCostResultDto
{
    [JsonPropertyName("amount")]
    public AnthropicAmountDto? Amount { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    [JsonPropertyName("workspace_id")]
    public string? WorkspaceId { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

public sealed class AnthropicAmountDto
{
    [JsonPropertyName("amount")]
    public string? Amount { get; init; }

    [JsonPropertyName("currency")]
    public string? Currency { get; init; }
}

public sealed record AnthropicCostsQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string BucketWidth,
    IReadOnlyCollection<string> GroupBy,
    IReadOnlyCollection<string> WorkspaceIds,
    int Limit);
