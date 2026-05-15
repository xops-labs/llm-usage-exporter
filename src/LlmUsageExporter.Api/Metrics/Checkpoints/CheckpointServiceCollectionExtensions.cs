// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Metrics.Checkpoints;

public static class CheckpointServiceCollectionExtensions
{
    public static IServiceCollection AddCheckpointStore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CheckpointStoreOptions>(configuration.GetSection(CheckpointStoreOptions.SectionName));
        services.PostConfigure<CheckpointStoreOptions>(options => ApplyEnvironment(options, configuration));

        services.AddSingleton<ICheckpointStore>(serviceProvider =>
        {
            CheckpointStoreOptions options = serviceProvider
                .GetRequiredService<IOptions<CheckpointStoreOptions>>()
                .Value;

            if (string.Equals(options.Provider, "File", StringComparison.OrdinalIgnoreCase))
            {
                ILogger<FileCheckpointStore> logger = serviceProvider
                    .GetRequiredService<ILogger<FileCheckpointStore>>();
                return new FileCheckpointStore(options, logger);
            }

            return new InMemoryCheckpointStore();
        });

        services.AddHostedService<CheckpointFlushHostedService>();

        return services;
    }

    private static void ApplyEnvironment(CheckpointStoreOptions options, IConfiguration configuration)
    {
        options.Provider = ReadString(configuration, "CHECKPOINTS_PROVIDER", options.Provider);
        options.FilePath = ReadString(configuration, "CHECKPOINTS_FILE_PATH", options.FilePath);
        options.MaxEntries = ReadInt(configuration, "CHECKPOINTS_MAX_ENTRIES", options.MaxEntries);
        options.RetentionHours = ReadInt(configuration, "CHECKPOINTS_RETENTION_HOURS", options.RetentionHours);
        options.FlushIntervalSeconds = ReadInt(configuration, "CHECKPOINTS_FLUSH_INTERVAL_SECONDS", options.FlushIntervalSeconds);
    }

    private static string ReadString(IConfiguration configuration, string key, string current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static int ReadInt(IConfiguration configuration, string key, int current)
    {
        string? value = configuration[key];
        return int.TryParse(value, out int parsed) ? parsed : current;
    }
}
