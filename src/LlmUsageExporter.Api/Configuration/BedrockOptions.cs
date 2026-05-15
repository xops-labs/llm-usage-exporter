// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class BedrockOptions
{
    public const string SectionName = "Bedrock";

    public string AccessKeyId { get; set; } = string.Empty;

    public string SecretAccessKey { get; set; } = string.Empty;

    public string? SessionToken { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string CostExplorerRegion { get; set; } = "us-east-1";

    public string[] ModelIds { get; set; } = [];

    public int MetricsPeriodSeconds { get; set; } = 3600;

    public bool EnableCostQueries { get; set; } = true;

    public string? CloudWatchEndpointOverride { get; set; }

    public string? CostExplorerEndpointOverride { get; set; }

    public int MaxRetries { get; set; } = 3;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;
}
