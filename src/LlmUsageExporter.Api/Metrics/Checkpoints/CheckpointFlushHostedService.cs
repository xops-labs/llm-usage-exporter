// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Metrics.Checkpoints;

public sealed class CheckpointFlushHostedService : BackgroundService
{
    private readonly ICheckpointStore _store;
    private readonly CheckpointStoreOptions _options;
    private readonly ILogger<CheckpointFlushHostedService> _logger;

    public CheckpointFlushHostedService(
        ICheckpointStore store,
        IOptions<CheckpointStoreOptions> options,
        ILogger<CheckpointFlushHostedService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int intervalSeconds = _options.FlushIntervalSeconds > 0 ? _options.FlushIntervalSeconds : 30;
        TimeSpan delay = TimeSpan.FromSeconds(intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await _store.FlushAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Checkpoint flush iteration failed.");
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _store.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Final checkpoint flush on shutdown failed.");
        }
    }
}
