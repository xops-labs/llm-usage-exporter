// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Alerts;

public sealed class AlertsOptions
{
    public const string SectionName = "Alerts";

    public bool Enabled { get; set; } = true;

    public int EvaluationIntervalSeconds { get; set; } = 60;

    public int RollingWindowBuckets { get; set; } = 24;

    public double AnomalyMinSamples { get; set; } = 6;

    public BudgetDefinition[] Budgets { get; set; } = Array.Empty<BudgetDefinition>();
}

public sealed class BudgetDefinition
{
    public string Name { get; set; } = string.Empty;

    // "daily" | "weekly" | "monthly"
    public string Period { get; set; } = "monthly";

    public decimal LimitUsd { get; set; }

    public string[] Providers { get; set; } = Array.Empty<string>();

    public string[] Models { get; set; } = Array.Empty<string>();

    public string[] Tenancies { get; set; } = Array.Empty<string>();

    public string[] Tenants { get; set; } = Array.Empty<string>();
}
