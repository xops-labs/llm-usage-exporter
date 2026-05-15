// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Metrics.Otlp;

internal static class LlmBucketIdentity
{
    public static string Build(
        string tenant,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string provider,
        string? model,
        string? tenancyId,
        string metricType)
    {
        return string.Join(
            "|",
            tenant,
            provider,
            startTime.ToUnixTimeSeconds(),
            endTime.ToUnixTimeSeconds(),
            MetricLabelSanitizer.Sanitize(model),
            MetricLabelSanitizer.Sanitize(tenancyId),
            metricType);
    }
}
