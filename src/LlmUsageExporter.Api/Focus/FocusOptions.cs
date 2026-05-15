// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Focus;

public sealed class FocusOptions
{
    public const string SectionName = "Focus";

    public bool Enabled { get; set; } = true;

    public int MaxRecords { get; set; } = 50_000;
}
