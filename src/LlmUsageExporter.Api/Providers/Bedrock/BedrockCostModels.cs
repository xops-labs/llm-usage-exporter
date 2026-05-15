// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Providers.Bedrock;

public sealed record BedrockCostsQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    string Granularity);

public sealed class CostExplorerResponse
{
    [JsonPropertyName("ResultsByTime")]
    public List<CostExplorerResultByTime>? ResultsByTime { get; init; }
}

public sealed class CostExplorerResultByTime
{
    [JsonPropertyName("TimePeriod")]
    public CostExplorerTimePeriod? TimePeriod { get; init; }

    [JsonPropertyName("Groups")]
    public List<CostExplorerGroup>? Groups { get; init; }

    [JsonPropertyName("Total")]
    public Dictionary<string, CostExplorerMetric>? Total { get; init; }
}

public sealed class CostExplorerTimePeriod
{
    [JsonPropertyName("Start")]
    public string? Start { get; init; }

    [JsonPropertyName("End")]
    public string? End { get; init; }
}

public sealed class CostExplorerGroup
{
    [JsonPropertyName("Keys")]
    public List<string>? Keys { get; init; }

    [JsonPropertyName("Metrics")]
    public Dictionary<string, CostExplorerMetric>? Metrics { get; init; }
}

public sealed class CostExplorerMetric
{
    [JsonPropertyName("Amount")]
    public string? Amount { get; init; }

    [JsonPropertyName("Unit")]
    public string? Unit { get; init; }
}
