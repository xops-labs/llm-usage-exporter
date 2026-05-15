// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace LlmUsageExporter.Api.Tracing;

public static class ExporterActivitySource
{
    public const string Name = "LlmUsageExporter";

    public static readonly ActivitySource Instance = new(Name);

    private static int _listenerRegistered;

    /// <summary>
    /// Registers a default ActivityListener so that <see cref="Instance"/> emits Activities
    /// even when no tracing exporter (OTLP) is configured. This allows outbound provider
    /// HttpClient calls to propagate W3C headers by default; <see cref="TraceContextSuppressionHandler"/>
    /// strips those headers when trace propagation is disabled.
    /// </summary>
    public static void EnsureListenerRegistered()
    {
        if (Interlocked.Exchange(ref _listenerRegistered, 1) != 0)
        {
            return;
        }

        ActivitySource.AddActivityListener(new ActivityListener
        {
            ShouldListenTo = source => source.Name == Name,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
        });
    }
}
