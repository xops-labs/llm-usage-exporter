// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public sealed class AnomalyDetector
{
    private const double ScoreCap = 10.0;

    private readonly object _sync = new();
    private readonly Dictionary<(string Tenant, string Provider, string Model), Queue<double>> _tokenHistory = new();
    private readonly Dictionary<(string Tenant, string Provider, string Model), Queue<double>> _costHistory = new();
    private readonly Dictionary<(string Tenant, string Provider, string Model), double> _latestTokenScore = new();
    private readonly Dictionary<(string Tenant, string Provider, string Model), double> _latestCostScore = new();

    public IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> EvaluateTokens(
        IReadOnlyCollection<LlmUsageBucket> buckets,
        int windowSize,
        double minSamples)
    {
        lock (_sync)
        {
            foreach (LlmUsageBucket bucket in buckets)
            {
                (string Tenant, string Provider, string Model) key = (
                    ResolveTenant(bucket.Tenant),
                    bucket.Provider,
                    bucket.Model ?? string.Empty);
                double score = ComputeAndAppend(_tokenHistory, key, bucket.TotalTokens, windowSize, minSamples);
                _latestTokenScore[key] = score;
            }

            return new Dictionary<(string, string, string), double>(_latestTokenScore);
        }
    }

    public IReadOnlyDictionary<(string Tenant, string Provider, string Model), double> EvaluateCost(
        IReadOnlyCollection<LlmCostBucket> buckets,
        int windowSize,
        double minSamples)
    {
        lock (_sync)
        {
            foreach (LlmCostBucket bucket in buckets)
            {
                (string Tenant, string Provider, string Model) key = (
                    ResolveTenant(bucket.Tenant),
                    bucket.Provider,
                    bucket.Model ?? string.Empty);
                double score = ComputeAndAppend(_costHistory, key, decimal.ToDouble(bucket.CostUsd), windowSize, minSamples);
                _latestCostScore[key] = score;
            }

            return new Dictionary<(string, string, string), double>(_latestCostScore);
        }
    }

    private static string ResolveTenant(string tenant)
    {
        return string.IsNullOrWhiteSpace(tenant) ? "default" : tenant;
    }

    private static double ComputeAndAppend(
        Dictionary<(string Tenant, string Provider, string Model), Queue<double>> history,
        (string Tenant, string Provider, string Model) key,
        double value,
        int windowSize,
        double minSamples)
    {
        if (!history.TryGetValue(key, out Queue<double>? queue))
        {
            queue = new Queue<double>(Math.Max(1, windowSize));
            history[key] = queue;
        }

        double score = 0.0;

        if (queue.Count >= minSamples)
        {
            double mean = 0.0;
            foreach (double sample in queue)
            {
                mean += sample;
            }
            mean /= queue.Count;

            double variance = 0.0;
            foreach (double sample in queue)
            {
                double delta = sample - mean;
                variance += delta * delta;
            }
            variance /= queue.Count;

            double stdev = Math.Sqrt(variance);
            if (stdev > 1e-9)
            {
                double raw = (value - mean) / stdev;
                if (raw > ScoreCap)
                {
                    score = ScoreCap;
                }
                else if (raw < -ScoreCap)
                {
                    score = -ScoreCap;
                }
                else
                {
                    score = raw;
                }
            }
            else
            {
                // Stable signal; non-zero deviation from constant baseline saturates to cap.
                if (value > mean)
                {
                    score = ScoreCap;
                }
                else if (value < mean)
                {
                    score = -ScoreCap;
                }
                else
                {
                    score = 0.0;
                }
            }
        }

        queue.Enqueue(value);
        while (queue.Count > windowSize)
        {
            queue.Dequeue();
        }

        return score;
    }
}
