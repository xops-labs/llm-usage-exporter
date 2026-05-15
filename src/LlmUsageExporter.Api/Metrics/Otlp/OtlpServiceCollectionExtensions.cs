// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Tracing;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LlmUsageExporter.Api.Metrics.Otlp;

public static class OtlpServiceCollectionExtensions
{
    public static IServiceCollection AddOtlpExport(this IServiceCollection services, IConfiguration configuration)
    {
        OtlpOptions options = new();
        configuration.GetSection(OtlpOptions.SectionName).Bind(options);
        ApplyEnvironment(options, configuration);

        options.Enabled = !string.IsNullOrWhiteSpace(options.Endpoint);
        services.AddSingleton(options);

        if (!options.Enabled)
        {
            return services;
        }

        services.AddSingleton<LlmMeterPublisher>();

        services
            .AddOpenTelemetry()
            .WithMetrics(metricsBuilder =>
            {
                metricsBuilder
                    .SetResourceBuilder(ResourceBuilder
                        .CreateDefault()
                        .AddService(serviceName: options.ServiceName, serviceVersion: options.ServiceVersion))
                    .AddMeter(LlmMeterPublisher.MeterName)
                    .AddOtlpExporter((exporterOptions, readerOptions) =>
                    {
                        exporterOptions.Endpoint = new Uri(options.Endpoint!);
                        exporterOptions.Protocol = ParseProtocol(options.Protocol);

                        if (!string.IsNullOrWhiteSpace(options.Headers))
                        {
                            exporterOptions.Headers = options.Headers;
                        }

                        readerOptions.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            Math.Max(options.ExportIntervalSeconds, 1) * 1000;
                    });
            })
            .WithTracing(tracingBuilder =>
            {
                tracingBuilder
                    .SetResourceBuilder(ResourceBuilder
                        .CreateDefault()
                        .AddService(serviceName: options.ServiceName, serviceVersion: options.ServiceVersion))
                    .AddSource(ExporterActivitySource.Name)
                    // .NET 8+ HttpClient emits activities under "System.Net.Http" — listen on it
                    // so each outbound provider call shows up as a child span and the built-in
                    // DiagnosticsHandler creates child HTTP spans. W3C propagation is enabled
                    // by default and can be stripped by TraceContextSuppressionHandler.
                    .AddSource("System.Net.Http")
                    .AddOtlpExporter(exporterOptions =>
                    {
                        exporterOptions.Endpoint = new Uri(options.Endpoint!);
                        exporterOptions.Protocol = ParseProtocol(options.Protocol);

                        if (!string.IsNullOrWhiteSpace(options.Headers))
                        {
                            exporterOptions.Headers = options.Headers;
                        }
                    });
            });

        return services;
    }

    private static OtlpExportProtocol ParseProtocol(string protocol)
    {
        if (string.IsNullOrWhiteSpace(protocol))
        {
            return OtlpExportProtocol.Grpc;
        }

        string normalized = protocol.Trim().ToLowerInvariant();
        return normalized switch
        {
            "grpc" => OtlpExportProtocol.Grpc,
            "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
            "http/protobuf" => OtlpExportProtocol.HttpProtobuf,
            "http" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc,
        };
    }

    private static void ApplyEnvironment(OtlpOptions options, IConfiguration configuration)
    {
        options.Endpoint = ReadOptionalString(configuration, "OTEL_EXPORTER_OTLP_ENDPOINT", options.Endpoint);
        options.Protocol = ReadString(configuration, "OTEL_EXPORTER_OTLP_PROTOCOL", options.Protocol);
        options.Headers = ReadOptionalString(configuration, "OTEL_EXPORTER_OTLP_HEADERS", options.Headers);
        options.ServiceName = ReadString(configuration, "OTEL_SERVICE_NAME", options.ServiceName);
        options.ServiceVersion = ReadString(configuration, "OTEL_SERVICE_VERSION", options.ServiceVersion);
        options.ExportIntervalSeconds = ReadInt(configuration, "OTEL_METRIC_EXPORT_INTERVAL_SECONDS", options.ExportIntervalSeconds);
        options.TracePropagationEnabled = ReadBool(configuration, "OTEL_TRACE_CONTEXT_PROPAGATION_ENABLED", options.TracePropagationEnabled);
    }

    private static string ReadString(IConfiguration configuration, string key, string current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static string? ReadOptionalString(IConfiguration configuration, string key, string? current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static int ReadInt(IConfiguration configuration, string key, int current)
    {
        string? value = configuration[key];
        return int.TryParse(value, out int parsed) ? parsed : current;
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool current)
    {
        string? value = configuration[key];
        return bool.TryParse(value?.Trim(), out bool parsed) ? parsed : current;
    }
}
