// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Metrics;

public static class MetricLabelSanitizer
{
    public const string Unknown = "unknown";

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unknown;
        }

        string trimmed = value.Trim();
        Span<char> buffer = trimmed.Length <= 512 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];

        for (int index = 0; index < trimmed.Length; index++)
        {
            char character = trimmed[index];
            buffer[index] = char.IsControl(character) ? '_' : character;
        }

        return new string(buffer);
    }
}
