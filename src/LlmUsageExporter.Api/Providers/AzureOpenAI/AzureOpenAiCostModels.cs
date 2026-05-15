// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.AzureOpenAI;

public sealed class AzureCostQueryRequest
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ActualCost";

    [JsonPropertyName("timeframe")]
    public string Timeframe { get; init; } = "Custom";

    [JsonPropertyName("timePeriod")]
    public AzureCostTimePeriod? TimePeriod { get; init; }

    [JsonPropertyName("dataset")]
    public AzureCostDataset? Dataset { get; init; }
}

public sealed class AzureCostTimePeriod
{
    [JsonPropertyName("from")]
    public string? From { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }
}

public sealed class AzureCostDataset
{
    [JsonPropertyName("granularity")]
    public string? Granularity { get; init; }

    [JsonPropertyName("aggregation")]
    public Dictionary<string, AzureCostAggregation>? Aggregation { get; init; }

    [JsonPropertyName("grouping")]
    public List<AzureCostGrouping>? Grouping { get; init; }

    [JsonPropertyName("filter")]
    public AzureCostFilter? Filter { get; init; }
}

public sealed class AzureCostAggregation
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("function")]
    public string? Function { get; init; }
}

public sealed class AzureCostGrouping
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class AzureCostFilter
{
    [JsonPropertyName("dimensions")]
    public AzureCostFilterDimensions? Dimensions { get; init; }
}

public sealed class AzureCostFilterDimensions
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("operator")]
    public string? Operator { get; init; }

    [JsonPropertyName("values")]
    public List<string>? Values { get; init; }
}

public sealed class AzureCostQueryResponse
{
    [JsonPropertyName("properties")]
    public AzureCostQueryProperties? Properties { get; init; }
}

public sealed class AzureCostQueryProperties
{
    [JsonPropertyName("columns")]
    public List<AzureCostQueryColumn>? Columns { get; init; }

    [JsonPropertyName("rows")]
    public List<List<object?>>? Rows { get; init; }
}

public sealed class AzureCostQueryColumn
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed record AzureOpenAiCostsQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Scope,
    string Granularity);
