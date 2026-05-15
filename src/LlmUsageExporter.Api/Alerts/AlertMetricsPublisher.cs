// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics;
using Prometheus;

namespace LlmUsageExporter.Api.Alerts;

public sealed class AlertMetricsPublisher
{
    private readonly Gauge _budgetBurnRatio;
    private readonly Gauge _budgetSpendUsd;
    private readonly Gauge _budgetLimitUsd;
    private readonly Gauge _budgetPeriodStartTimestamp;
    private readonly Gauge _costAnomalyScore;
    private readonly Gauge _tokenAnomalyScore;

    public AlertMetricsPublisher()
        : this(Prometheus.Metrics.DefaultFactory)
    {
    }

    public AlertMetricsPublisher(IMetricFactory metricFactory)
    {
        _budgetBurnRatio = metricFactory.CreateGauge(
            "llm_alerts_budget_burn_ratio",
            "Current burn ratio (spend divided by limit) for the configured budget.",
            new GaugeConfiguration { LabelNames = ["budget_name"] });

        _budgetSpendUsd = metricFactory.CreateGauge(
            "llm_alerts_budget_spend_usd",
            "Running spend in USD for the current budget period.",
            new GaugeConfiguration { LabelNames = ["budget_name"] });

        _budgetLimitUsd = metricFactory.CreateGauge(
            "llm_alerts_budget_limit_usd",
            "Configured spending limit in USD for the budget.",
            new GaugeConfiguration { LabelNames = ["budget_name"] });

        _budgetPeriodStartTimestamp = metricFactory.CreateGauge(
            "llm_alerts_budget_period_start_timestamp",
            "Unix timestamp marking the start of the current budget period.",
            new GaugeConfiguration { LabelNames = ["budget_name"] });

        _costAnomalyScore = metricFactory.CreateGauge(
            "llm_alerts_cost_anomaly_score",
            "Z-score of the latest cost bucket against the rolling baseline window. Clamped to [-10, 10].",
            new GaugeConfiguration { LabelNames = ["tenant", "provider", "model"] });

        _tokenAnomalyScore = metricFactory.CreateGauge(
            "llm_alerts_token_anomaly_score",
            "Z-score of the latest token throughput against the rolling baseline window. Clamped to [-10, 10].",
            new GaugeConfiguration { LabelNames = ["tenant", "provider", "model"] });
    }

    public void PublishBudgets(IReadOnlyList<BudgetEvaluation> evaluations)
    {
        foreach (BudgetEvaluation evaluation in evaluations)
        {
            string name = MetricLabelSanitizer.Sanitize(evaluation.Name);
            _budgetBurnRatio.WithLabels(name).Set(evaluation.BurnRatio);
            _budgetSpendUsd.WithLabels(name).Set(decimal.ToDouble(evaluation.Spend));
            _budgetLimitUsd.WithLabels(name).Set(decimal.ToDouble(evaluation.Limit));
            _budgetPeriodStartTimestamp.WithLabels(name).Set(evaluation.PeriodStart.ToUnixTimeSeconds());
        }
    }

    public void PublishTokenAnomalies(IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> scores)
    {
        foreach (KeyValuePair<(string Tenant, string Provider, string Model), double> entry in scores)
        {
            string tenant = MetricLabelSanitizer.Sanitize(entry.Key.Tenant);
            string provider = MetricLabelSanitizer.Sanitize(entry.Key.Provider);
            string model = MetricLabelSanitizer.Sanitize(entry.Key.Model);
            _tokenAnomalyScore.WithLabels(tenant, provider, model).Set(entry.Value);
        }
    }

    public void PublishCostAnomalies(IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> scores)
    {
        foreach (KeyValuePair<(string Tenant, string Provider, string Model), double> entry in scores)
        {
            string tenant = MetricLabelSanitizer.Sanitize(entry.Key.Tenant);
            string provider = MetricLabelSanitizer.Sanitize(entry.Key.Provider);
            string model = MetricLabelSanitizer.Sanitize(entry.Key.Model);
            _costAnomalyScore.WithLabels(tenant, provider, model).Set(entry.Value);
        }
    }
}
