// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Providers.Demo;

public static class DemoServiceCollectionExtensions
{
    public static IServiceCollection AddDemoProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        if (!IsEnabled(configuration))
        {
            return services;
        }

        services.AddSingleton<DemoUsageProvider>();

        services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
        {
            ILogger<DemoUsageProvider> logger = serviceProvider.GetRequiredService<ILogger<DemoUsageProvider>>();
            logger.LogWarning(
                "Demo mode ENABLED — emitting synthetic data labeled provider=\"{Provider}\". " +
                "Disable by unsetting DEMO_MODE_ENABLED before any production use.",
                DemoUsageProvider.ProviderName);

            ICheckpointStore checkpointStore = serviceProvider.GetRequiredService<ICheckpointStore>();
            LlmMetricsPublisher prometheus = new(
                DemoUsageProvider.ProviderName,
                Prometheus.Metrics.DefaultFactory,
                checkpointStore,
                TenantContext.Default);

            LlmMeterPublisher? otel = serviceProvider.GetService<LlmMeterPublisher>();
            ILlmMetricsPublisher publisher = otel is null
                ? prometheus
                : new CompositeMetricsPublisher(
                    new ILlmMetricsPublisher[] { prometheus, otel },
                    serviceProvider.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

            return new LlmProviderRegistration(
                DemoUsageProvider.ProviderName,
                serviceProvider.GetRequiredService<DemoUsageProvider>(),
                publisher);
        });

        return services;
    }

    private static bool IsEnabled(IConfiguration configuration)
    {
        string? env = configuration["DEMO_MODE_ENABLED"];
        if (bool.TryParse(env, out bool envEnabled) && envEnabled)
        {
            return true;
        }

        string? setting = configuration["Demo:Enabled"];
        return bool.TryParse(setting, out bool settingsEnabled) && settingsEnabled;
    }
}
