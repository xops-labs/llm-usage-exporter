// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;

namespace LlmUsageExporter.Tests;

public sealed class MetricLabelSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_ReturnsUnknownForEmptyValues(string? value)
    {
        Assert.Equal("unknown", MetricLabelSanitizer.Sanitize(value));
    }

    [Fact]
    public void Sanitize_TrimsAndReplacesControlCharacters()
    {
        string sanitized = MetricLabelSanitizer.Sanitize("  gpt\n4o\tmini  ");

        Assert.Equal("gpt_4o_mini", sanitized);
    }
}
