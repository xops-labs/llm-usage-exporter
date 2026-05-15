// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class GeminiOptions
{
    public const string SectionName = "Gemini";

    public string MonitoringBaseUrl { get; set; } = "https://monitoring.googleapis.com";

    public string BigQueryBaseUrl { get; set; } = "https://bigquery.googleapis.com";

    public string OAuthBaseUrl { get; set; } = "https://oauth2.googleapis.com";

    public string ProjectId { get; set; } = string.Empty;

    public string? BillingProjectId { get; set; }

    public string? BillingDatasetProject { get; set; }

    public string? BillingDatasetId { get; set; }

    public string? BillingTable { get; set; }

    public bool EnableCostQueries { get; set; }

    public string? AccessToken { get; set; }

    public string? ServiceAccountKeyFile { get; set; }

    public string[] Models { get; set; } = [];

    public int UsageAlignmentPeriodSeconds { get; set; } = 3600;

    public int MaxRetries { get; set; } = 3;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;
}
