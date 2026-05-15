// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Focus;

public static class FocusServiceCollectionExtensions
{
    public static IServiceCollection AddFocusExport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<FocusOptions>(configuration.GetSection(FocusOptions.SectionName));
        services.PostConfigure<FocusOptions>(options => ApplyEnvironment(options, configuration));

        FocusOptions snapshot = new();
        configuration.GetSection(FocusOptions.SectionName).Bind(snapshot);
        ApplyEnvironment(snapshot, configuration);

        if (!snapshot.Enabled)
        {
            return services;
        }

        services.AddSingleton<IFocusRecordStore, InMemoryFocusRecordStore>();
        services.AddSingleton<IRegistrationDecorator, FocusObservingRegistrationDecorator>();

        return services;
    }

    private static void ApplyEnvironment(FocusOptions options, IConfiguration configuration)
    {
        string? enabled = configuration["FOCUS_ENABLED"];
        if (!string.IsNullOrWhiteSpace(enabled) && bool.TryParse(enabled, out bool parsedEnabled))
        {
            options.Enabled = parsedEnabled;
        }

        string? maxRecords = configuration["FOCUS_MAX_RECORDS"];
        if (!string.IsNullOrWhiteSpace(maxRecords) && int.TryParse(maxRecords, out int parsedMax) && parsedMax > 0)
        {
            options.MaxRecords = parsedMax;
        }
    }
}
