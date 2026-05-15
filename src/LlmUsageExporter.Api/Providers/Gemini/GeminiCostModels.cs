// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.Gemini;

public sealed class GeminiBigQueryResponse
{
    [JsonPropertyName("schema")]
    public GeminiBigQuerySchema? Schema { get; init; }

    [JsonPropertyName("rows")]
    public List<GeminiBigQueryRow>? Rows { get; init; }

    [JsonPropertyName("jobComplete")]
    public bool? JobComplete { get; init; }

    [JsonPropertyName("totalRows")]
    public string? TotalRows { get; init; }
}

public sealed class GeminiBigQuerySchema
{
    [JsonPropertyName("fields")]
    public List<GeminiBigQueryField>? Fields { get; init; }
}

public sealed class GeminiBigQueryField
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class GeminiBigQueryRow
{
    [JsonPropertyName("f")]
    public List<GeminiBigQueryCell>? Fields { get; init; }
}

public sealed class GeminiBigQueryCell
{
    [JsonPropertyName("v")]
    public object? Value { get; init; }
}

public sealed record GeminiCostsQuery(
    DateTimeOffset Start,
    DateTimeOffset End);
