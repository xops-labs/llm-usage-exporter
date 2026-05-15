// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class OtlpOptions
{
    public const string SectionName = "Otel";

    public string? Endpoint { get; set; }

    public string Protocol { get; set; } = "grpc";

    public string? Headers { get; set; }

    public string ServiceName { get; set; } = "llm-usage-exporter";

    public string ServiceVersion { get; set; } = "0.2.0";

    public int ExportIntervalSeconds { get; set; } = 60;

    public bool Enabled { get; set; }

    /// <summary>
    /// When true (default), W3C traceparent/tracestate headers are propagated on
    /// outbound provider HTTP calls. Set to false if your organization's policy prohibits
    /// sending trace IDs across vendor boundaries (privacy/governance).
    /// Controlled via OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED env var.
    /// </summary>
    public bool TracePropagationEnabled { get; set; } = true;
}
