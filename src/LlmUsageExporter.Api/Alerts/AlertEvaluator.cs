// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Alerts;

public sealed class AlertEvaluator : BackgroundService
{
    private readonly IAlertSource _source;
    private readonly BudgetEvaluator _budgetEvaluator;
    private readonly AnomalyDetector _anomalyDetector;
    private readonly AlertMetricsPublisher _metricsPublisher;
    private readonly IOptionsMonitor<AlertsOptions> _options;
    private readonly ILogger<AlertEvaluator> _logger;

    public AlertEvaluator(
        IAlertSource source,
        BudgetEvaluator budgetEvaluator,
        AnomalyDetector anomalyDetector,
        AlertMetricsPublisher metricsPublisher,
        IOptionsMonitor<AlertsOptions> options,
        ILogger<AlertEvaluator> logger)
    {
        _source = source;
        _budgetEvaluator = budgetEvaluator;
        _anomalyDetector = anomalyDetector;
        _metricsPublisher = metricsPublisher;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AlertsOptions options = _options.CurrentValue;
        if (!options.Enabled)
        {
            _logger.LogInformation("AlertEvaluator is disabled via configuration.");
            return;
        }

        _logger.LogInformation(
            "Starting AlertEvaluator with interval {IntervalSeconds}s, rolling window {Window}, min samples {MinSamples}",
            options.EvaluationIntervalSeconds,
            options.RollingWindowBuckets,
            options.AnomalyMinSamples);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan interval = TimeSpan.FromSeconds(Math.Max(_options.CurrentValue.EvaluationIntervalSeconds, 1));

            try
            {
                await Task.Delay(interval, stoppingToken);
                EvaluateOnce(DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "AlertEvaluator iteration failed.");
            }
        }
    }

    public void EvaluateOnce(DateTimeOffset now)
    {
        AlertsOptions options = _options.CurrentValue;
        if (!options.Enabled)
        {
            return;
        }

        IReadOnlyCollection<LlmUsageBucket> usage = _source.DrainUsage();
        IReadOnlyCollection<LlmCostBucket> cost = _source.DrainCost();

        int window = Math.Max(options.RollingWindowBuckets, 1);
        double minSamples = Math.Max(options.AnomalyMinSamples, 1);

        IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> tokenScores = _anomalyDetector.EvaluateTokens(usage, window, minSamples);
        IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> costScores = _anomalyDetector.EvaluateCost(cost, window, minSamples);

        IReadOnlyList<BudgetEvaluation> budgetEvaluations = _budgetEvaluator.Evaluate(cost, options.Budgets, now);

        _metricsPublisher.PublishBudgets(budgetEvaluations);
        _metricsPublisher.PublishTokenAnomalies(tokenScores);
        _metricsPublisher.PublishCostAnomalies(costScores);
    }
}
