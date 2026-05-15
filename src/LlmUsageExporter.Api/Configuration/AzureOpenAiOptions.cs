// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class AzureOpenAiOptions
{
    public const string SectionName = "AzureOpenAI";

    public string TenantId { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public string SubscriptionId { get; set; } = string.Empty;

    public string? ResourceGroup { get; set; }

    public string[] AccountResourceIds { get; set; } = [];

    public string[] Models { get; set; } = [];

    public string UsageInterval { get; set; } = "PT1H";

    public string CostsGranularity { get; set; } = "Daily";

    public string ManagementBaseUrl { get; set; } = "https://management.azure.com";

    public string LoginBaseUrl { get; set; } = "https://login.microsoftonline.com";

    public int MaxRetries { get; set; } = 3;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;
}
