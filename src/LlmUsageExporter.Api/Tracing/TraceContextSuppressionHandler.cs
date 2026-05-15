// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using LlmUsageExporter.Api.Configuration;

namespace LlmUsageExporter.Api.Tracing;

/// <summary>
/// Strips W3C traceparent/tracestate propagation from outbound provider HTTP calls when
/// <see cref="OtlpOptions.TracePropagationEnabled"/> is false.
///
/// Mechanism: .NET's built-in DiagnosticsHandler only injects propagation headers when
/// Activity.Current is non-null. Clearing it for the duration of the downstream send
/// prevents injection without touching the OTLP pipeline or the caller's span.
/// </summary>
public sealed class TraceContextSuppressionHandler : DelegatingHandler
{
    private readonly bool _propagationEnabled;

    public TraceContextSuppressionHandler(OtlpOptions options)
    {
        _propagationEnabled = options.TracePropagationEnabled;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_propagationEnabled)
        {
            return base.SendAsync(request, cancellationToken);
        }

        return SendWithoutPropagationAsync(request, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithoutPropagationAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Activity? saved = Activity.Current;
        Activity.Current = null;
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            Activity.Current = saved;
        }
    }
}
