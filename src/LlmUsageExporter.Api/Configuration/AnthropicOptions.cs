// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    public string AdminApiKey { get; set; } = string.Empty;

    public string AnthropicVersion { get; set; } = "2023-06-01";

    public string[] WorkspaceIds { get; set; } = [];

    public string[] Models { get; set; } = [];

    public string[] ApiKeyIds { get; set; } = [];

    public string[] GroupBy { get; set; } = ["model", "workspace_id"];

    public string UsageBucketWidth { get; set; } = "1h";

    public string CostsBucketWidth { get; set; } = "1d";

    public int UsagePageLimit { get; set; } = 168;

    public int CostsPageLimit { get; set; } = 180;

    public int MaxRetries { get; set; } = 3;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;
}
