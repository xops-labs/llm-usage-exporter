// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Health;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Tracing;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Workers;

public sealed class UsagePollingWorker : BackgroundService
{
    private readonly IReadOnlyList<LlmProviderRegistration> _registrations;
    private readonly ExporterHealthState _healthState;
    private readonly IOptionsMonitor<ExporterOptions> _options;
    private readonly ILogger<UsagePollingWorker> _logger;

    public UsagePollingWorker(
        IEnumerable<LlmProviderRegistration> registrations,
        IEnumerable<IRegistrationDecorator> decorators,
        ExporterHealthState healthState,
        IOptionsMonitor<ExporterOptions> options,
        ILogger<UsagePollingWorker> logger)
    {
        IRegistrationDecorator[] decoratorArray = decorators.ToArray();
        _registrations = registrations
            .Select(registration => decoratorArray.Aggregate(registration, (acc, decorator) => decorator.Decorate(acc)))
            .ToArray();
        _healthState = healthState;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_registrations.Count == 0)
        {
            _logger.LogWarning("No LLM providers are registered; polling worker will idle.");
        }
        else
        {
            _logger.LogInformation(
                "Starting usage polling worker for providers: {Providers}",
                string.Join(", ", _registrations.Select(r => $"{r.Tenant.Id}/{r.Name}")));
        }

        await PollOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.PollIntervalSeconds, 1));

            try
            {
                await Task.Delay(interval, stoppingToken);
                await PollOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        ExporterOptions options = _options.CurrentValue;
        DateTimeOffset end = DateTimeOffset.UtcNow;
        DateTimeOffset start = end.AddMinutes(-Math.Max(options.LookbackMinutes, 1));

        bool anySuccess = false;
        bool anyFailure = false;
        Exception? lastFailure = null;

        foreach (LlmProviderRegistration registration in _registrations)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Start a per-provider Activity so outbound provider calls can carry
            // W3C trace context by default. TraceContextSuppressionHandler strips
            // propagation headers when governance config disables cross-vendor flow.
            using Activity? activity = ExporterActivitySource.Instance.StartActivity(
                name: $"poll {registration.Name}",
                kind: ActivityKind.Client);

            activity?.SetTag("llm.provider", registration.Name);
            activity?.SetTag("llm.tenant", registration.Tenant.Id);
            activity?.SetTag("llm.window.start", start.ToString("o"));
            activity?.SetTag("llm.window.end", end.ToString("o"));

            try
            {
                _logger.LogInformation(
                    "Polling {Tenant}/{Provider} usage and costs for window {StartTime:o} to {EndTime:o}",
                    registration.Tenant.Id,
                    registration.Name,
                    start,
                    end);

                IReadOnlyCollection<LlmUsageBucket> usageBuckets = await registration.Provider.GetUsageAsync(start, end, cancellationToken);
                IReadOnlyCollection<LlmCostBucket> costBuckets = await registration.Provider.GetCostsAsync(start, end, cancellationToken);

                registration.Publisher.PublishUsage(usageBuckets);
                registration.Publisher.PublishCosts(costBuckets);

                stopwatch.Stop();
                DateTimeOffset successTimestamp = DateTimeOffset.UtcNow;
                registration.Publisher.RecordPollSuccess(successTimestamp, stopwatch.Elapsed);

                activity?.SetTag("llm.usage.bucket_count", usageBuckets.Count);
                activity?.SetTag("llm.cost.bucket_count", costBuckets.Count);
                activity?.SetStatus(ActivityStatusCode.Ok);

                _logger.LogInformation(
                    "{Tenant}/{Provider} polling succeeded with {UsageBucketCount} usage buckets and {CostBucketCount} cost buckets in {DurationSeconds:F3}s",
                    registration.Tenant.Id,
                    registration.Name,
                    usageBuckets.Count,
                    costBuckets.Count,
                    stopwatch.Elapsed.TotalSeconds);

                anySuccess = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                registration.Publisher.RecordPollFailure(DateTimeOffset.UtcNow, stopwatch.Elapsed);

                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                activity?.AddEvent(new ActivityEvent("poll.exception", default, new ActivityTagsCollection
                {
                    { "exception.type", exception.GetType().FullName },
                    { "exception.message", exception.Message },
                }));

                _logger.LogError(
                    exception,
                    "{Tenant}/{Provider} polling failed after {DurationSeconds:F3}s",
                    registration.Tenant.Id,
                    registration.Name,
                    stopwatch.Elapsed.TotalSeconds);

                anyFailure = true;
                lastFailure = exception;
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (anySuccess && !anyFailure)
        {
            _healthState.RecordSuccess(now);
        }
        else if (anyFailure && lastFailure is not null)
        {
            _healthState.RecordFailure(lastFailure, now);
        }
        else if (_registrations.Count == 0)
        {
            _healthState.RecordSuccess(now);
        }
    }
}
