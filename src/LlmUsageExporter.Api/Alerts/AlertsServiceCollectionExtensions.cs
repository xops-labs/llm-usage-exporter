// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public static class AlertsServiceCollectionExtensions
{
    public static IServiceCollection AddAlerts(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<AlertsOptions>(configuration.GetSection(AlertsOptions.SectionName));
        services.PostConfigure<AlertsOptions>(options => ApplyEnvironment(options, configuration));

        services
            .AddOptions<AlertsOptions>()
            .Validate(options => options.EvaluationIntervalSeconds > 0, "Alerts:EvaluationIntervalSeconds must be greater than zero.")
            .Validate(options => options.RollingWindowBuckets > 0, "Alerts:RollingWindowBuckets must be greater than zero.")
            .Validate(options => options.AnomalyMinSamples >= 1, "Alerts:AnomalyMinSamples must be greater than or equal to one.")
            .ValidateOnStart();

        services.AddSingleton<IAlertSource, InMemoryAlertSource>();
        services.AddSingleton<BudgetEvaluator>();
        services.AddSingleton<AnomalyDetector>();
        services.AddSingleton<AlertMetricsPublisher>();
        services.AddSingleton<IRegistrationDecorator, AlertObservingRegistrationDecorator>();
        services.AddHostedService<AlertEvaluator>();

        return services;
    }

    private static void ApplyEnvironment(AlertsOptions options, IConfiguration configuration)
    {
        options.Enabled = ReadBool(configuration, "ALERTS_ENABLED", options.Enabled);
        options.EvaluationIntervalSeconds = ReadInt(configuration, "ALERTS_EVALUATION_INTERVAL_SECONDS", options.EvaluationIntervalSeconds);
        options.RollingWindowBuckets = ReadInt(configuration, "ALERTS_ROLLING_WINDOW_BUCKETS", options.RollingWindowBuckets);
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool current)
    {
        string? value = configuration[key];
        return bool.TryParse(value, out bool parsed) ? parsed : current;
    }

    private static int ReadInt(IConfiguration configuration, string key, int current)
    {
        string? value = configuration[key];
        return int.TryParse(value, out int parsed) ? parsed : current;
    }
}
