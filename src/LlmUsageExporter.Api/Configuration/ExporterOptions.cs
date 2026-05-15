// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class ExporterOptions
{
    public const string SectionName = "Exporter";

    public int PollIntervalSeconds { get; set; } = 300;

    public int LookbackMinutes { get; set; } = 60;

    public int FailureThreshold { get; set; } = 3;
}
