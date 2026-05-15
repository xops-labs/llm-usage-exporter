// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Bedrock;

public sealed class BedrockUsageProvider : ILlmUsageProvider
{
    private const string ProviderName = "bedrock";

    private static readonly string[] ModelHintFragments =
    [
        "anthropic", "claude", "meta", "llama", "mistral", "cohere", "titan", "nova",
    ];

    private readonly BedrockUsageClient _client;
    private readonly BedrockOptions _options;
    private readonly TenantContext _tenant;

    public BedrockUsageProvider(BedrockUsageClient client, IOptions<BedrockOptions> options)
        : this(client, options.Value, TenantContext.Default)
    {
    }

    public BedrockUsageProvider(BedrockUsageClient client, BedrockOptions options, TenantContext tenant)
    {
        _client = client;
        _options = options;
        _tenant = tenant ?? TenantContext.Default;
    }

    public async Task<IReadOnlyCollection<LlmUsageBucket>> GetUsageAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var query = new BedrockUsageQuery(
            start,
            end,
            _options.ModelIds,
            Math.Max(_options.MetricsPeriodSeconds, 60));

        IReadOnlyList<BedrockMetricDataResult> results = await _client.GetCloudWatchMetricDataAsync(query, cancellationToken);
        if (results.Count == 0)
        {
            return Array.Empty<LlmUsageBucket>();
        }

        // Group results by Label (model id) and aggregate per timestamp bucket.
        Dictionary<string, ModelAggregator> byModel = new(StringComparer.Ordinal);

        foreach (BedrockMetricDataResult result in results)
        {
            string modelKey = string.IsNullOrWhiteSpace(result.Label) ? "unknown" : result.Label;
            if (!byModel.TryGetValue(modelKey, out ModelAggregator? aggregator))
            {
                aggregator = new ModelAggregator();
                byModel[modelKey] = aggregator;
            }

            string metricKind = result.Id.Split('|', 2)[0];
            int count = Math.Min(result.Timestamps.Count, result.Values.Count);
            for (int index = 0; index < count; index++)
            {
                DateTimeOffset bucketStart = result.Timestamps[index];
                aggregator.Add(bucketStart, metricKind, result.Values[index]);
            }
        }

        List<LlmUsageBucket> buckets = [];
        foreach ((string modelKey, ModelAggregator aggregator) in byModel)
        {
            foreach ((DateTimeOffset bucketStart, MetricTotals totals) in aggregator.Totals)
            {
                DateTimeOffset bucketEnd = bucketStart.AddSeconds(query.PeriodSeconds);
                long inputTokens = ToLong(totals.InputTokens);
                long outputTokens = ToLong(totals.OutputTokens);
                long requests = ToLong(totals.Invocations);

                buckets.Add(new LlmUsageBucket(
                    bucketStart,
                    bucketEnd,
                    ProviderName,
                    modelKey,
                    _options.Region,
                    inputTokens,
                    outputTokens,
                    CachedInputTokens: 0,
                    TotalTokens: inputTokens + outputTokens,
                    RequestCount: requests)
                {
                    Tenant = _tenant.Id
                });
            }
        }

        return buckets;
    }

    public async Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableCostQueries)
        {
            return Array.Empty<LlmCostBucket>();
        }

        var query = new BedrockCostsQuery(start, end, "DAILY");
        CostExplorerResponse response = await _client.GetCostExplorerCostsAsync(query, cancellationToken);

        if (response.ResultsByTime is null || response.ResultsByTime.Count == 0)
        {
            return Array.Empty<LlmCostBucket>();
        }

        List<LlmCostBucket> buckets = [];
        foreach (CostExplorerResultByTime result in response.ResultsByTime)
        {
            if (result.TimePeriod is null
                || !DateTimeOffset.TryParse(result.TimePeriod.Start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset bucketStart)
                || !DateTimeOffset.TryParse(result.TimePeriod.End, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset bucketEnd))
            {
                continue;
            }

            foreach (CostExplorerGroup group in result.Groups ?? [])
            {
                string usageType = group.Keys is { Count: > 0 } ? group.Keys[0] : "unknown";
                string model = ExtractModelFromUsageType(usageType);

                decimal costUsd = 0m;
                if (group.Metrics is not null
                    && group.Metrics.TryGetValue("UnblendedCost", out CostExplorerMetric? metric)
                    && string.Equals(metric.Unit, "USD", StringComparison.OrdinalIgnoreCase)
                    && decimal.TryParse(metric.Amount, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal amount))
                {
                    costUsd = amount;
                }

                buckets.Add(new LlmCostBucket(
                    bucketStart,
                    bucketEnd,
                    ProviderName,
                    model,
                    _options.Region,
                    costUsd)
                {
                    Tenant = _tenant.Id
                });
            }
        }

        return buckets;
    }

    private static string ExtractModelFromUsageType(string usageType)
    {
        if (string.IsNullOrWhiteSpace(usageType))
        {
            return "unknown";
        }

        string lower = usageType.ToLowerInvariant();
        foreach (string fragment in ModelHintFragments)
        {
            int index = lower.IndexOf(fragment, StringComparison.Ordinal);
            if (index >= 0)
            {
                // Return a sensible substring starting at the fragment, up to end of token (stop at next dash separator after the fragment word block).
                int tail = lower.Length;
                int dashIndex = lower.IndexOf('-', index + fragment.Length);
                int slashIndex = lower.IndexOf('/', index + fragment.Length);
                int spaceIndex = lower.IndexOf(' ', index + fragment.Length);

                int firstDelimiter = tail;
                foreach (int candidate in new[] { dashIndex, slashIndex, spaceIndex })
                {
                    if (candidate >= 0 && candidate < firstDelimiter)
                    {
                        firstDelimiter = candidate;
                    }
                }

                // Walk back to a leading delimiter so we pick the whole model token.
                int start = index;
                while (start > 0 && lower[start - 1] != '-' && lower[start - 1] != '/' && lower[start - 1] != ' ')
                {
                    start--;
                }

                return usageType.Substring(start, firstDelimiter - start);
            }
        }

        return usageType;
    }

    private static long ToLong(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }
        return (long)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private sealed class ModelAggregator
    {
        public Dictionary<DateTimeOffset, MetricTotals> Totals { get; } = new();

        public void Add(DateTimeOffset bucketStart, string metricKind, double value)
        {
            if (!Totals.TryGetValue(bucketStart, out MetricTotals? totals))
            {
                totals = new MetricTotals();
                Totals[bucketStart] = totals;
            }

            switch (metricKind)
            {
                case "InputTokenCount":
                    totals.InputTokens += value;
                    break;
                case "OutputTokenCount":
                    totals.OutputTokens += value;
                    break;
                case "Invocations":
                    totals.Invocations += value;
                    break;
            }
        }
    }

    private sealed class MetricTotals
    {
        public double InputTokens { get; set; }

        public double OutputTokens { get; set; }

        public double Invocations { get; set; }
    }
}
