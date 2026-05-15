// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.AzureOpenAI;

public sealed class AzureOpenAiUsageProvider : ILlmUsageProvider
{
    private const string ProviderName = "azure_openai";
    private const string ProcessedPromptTokensMetric = "ProcessedPromptTokens";
    private const string GeneratedTokensMetric = "GeneratedTokens";
    private const string TokenTransactionMetric = "TokenTransaction";
    private const string DeploymentDimensionName = "ModelDeploymentName";

    private readonly AzureOpenAiUsageClient _client;
    private readonly AzureOpenAiOptions _options;
    private readonly TenantContext _tenant;

    public AzureOpenAiUsageProvider(AzureOpenAiUsageClient client, IOptions<AzureOpenAiOptions> options)
        : this(client, options.Value, TenantContext.Default)
    {
    }

    public AzureOpenAiUsageProvider(AzureOpenAiUsageClient client, AzureOpenAiOptions options, TenantContext tenant)
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
        if (_options.AccountResourceIds.Length == 0)
        {
            return Array.Empty<LlmUsageBucket>();
        }

        var query = new AzureOpenAiUsageQuery(
            start,
            end,
            _options.AccountResourceIds,
            _options.Models,
            _options.UsageInterval);

        List<LlmUsageBucket> results = [];

        foreach (string accountResourceId in _options.AccountResourceIds)
        {
            if (string.IsNullOrWhiteSpace(accountResourceId))
            {
                continue;
            }

            AzureMetricsResponse response = await _client.GetMetricsAsync(accountResourceId, query, cancellationToken);
            results.AddRange(MapMetricsResponse(response, accountResourceId));
        }

        return results;
    }

    public async Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        string scope = BuildScope();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Array.Empty<LlmCostBucket>();
        }

        var query = new AzureOpenAiCostsQuery(start, end, scope, _options.CostsGranularity);
        AzureCostQueryResponse response = await _client.GetCostsAsync(query, cancellationToken);

        return MapCostResponse(response, start, end);
    }

    private string BuildScope()
    {
        if (string.IsNullOrWhiteSpace(_options.SubscriptionId))
        {
            return string.Empty;
        }

        string baseScope = $"subscriptions/{_options.SubscriptionId.Trim()}";
        return string.IsNullOrWhiteSpace(_options.ResourceGroup)
            ? baseScope
            : $"{baseScope}/resourceGroups/{_options.ResourceGroup.Trim()}";
    }

    private IEnumerable<LlmUsageBucket> MapMetricsResponse(AzureMetricsResponse response, string accountResourceId)
    {
        if (response.Value is null || response.Value.Count == 0)
        {
            yield break;
        }

        Dictionary<(string Deployment, DateTimeOffset Bucket), AggregatedBucket> aggregated = new();

        foreach (AzureMetric metric in response.Value)
        {
            string metricName = metric.Name?.Value ?? string.Empty;
            if (metric.Timeseries is null)
            {
                continue;
            }

            foreach (AzureMetricTimeseries series in metric.Timeseries)
            {
                string deployment = ResolveDeployment(series, metricName);

                if (_options.Models.Length > 0 && !_options.Models.Contains(deployment, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (series.Data is null)
                {
                    continue;
                }

                foreach (AzureMetricDatapoint datapoint in series.Data)
                {
                    if (datapoint.TimeStamp is null || datapoint.Total is null)
                    {
                        continue;
                    }

                    DateTimeOffset bucketStart = datapoint.TimeStamp.Value;
                    var key = (deployment, bucketStart);

                    if (!aggregated.TryGetValue(key, out AggregatedBucket? existing))
                    {
                        existing = new AggregatedBucket(bucketStart, bucketStart + ResolveInterval());
                        aggregated[key] = existing;
                    }

                    long value = (long)Math.Round(datapoint.Total.Value);

                    switch (metricName)
                    {
                        case ProcessedPromptTokensMetric:
                            existing.InputTokens += value;
                            break;
                        case GeneratedTokensMetric:
                            existing.OutputTokens += value;
                            break;
                        case TokenTransactionMetric:
                            existing.RequestCount += value;
                            break;
                    }
                }
            }
        }

        foreach (((string deployment, _), AggregatedBucket bucket) in aggregated)
        {
            yield return new LlmUsageBucket(
                bucket.Start,
                bucket.End,
                ProviderName,
                deployment,
                accountResourceId,
                bucket.InputTokens,
                bucket.OutputTokens,
                0,
                bucket.InputTokens + bucket.OutputTokens,
                bucket.RequestCount)
            {
                Tenant = _tenant.Id
            };
        }
    }

    private TimeSpan ResolveInterval()
    {
        string interval = _options.UsageInterval ?? "PT1H";
        return interval switch
        {
            "PT5M" => TimeSpan.FromMinutes(5),
            "PT15M" => TimeSpan.FromMinutes(15),
            "PT30M" => TimeSpan.FromMinutes(30),
            "PT1H" => TimeSpan.FromHours(1),
            "PT6H" => TimeSpan.FromHours(6),
            "PT12H" => TimeSpan.FromHours(12),
            "P1D" => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };
    }

    private static string ResolveDeployment(AzureMetricTimeseries series, string metricName)
    {
        if (series.MetadataValues is not null)
        {
            foreach (AzureMetricMetadataValue metadata in series.MetadataValues)
            {
                if (string.Equals(metadata.Name?.Value, DeploymentDimensionName, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(metadata.Value))
                {
                    return metadata.Value;
                }
            }
        }

        return metricName;
    }

    private IReadOnlyCollection<LlmCostBucket> MapCostResponse(
        AzureCostQueryResponse response,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        if (response.Properties?.Rows is null || response.Properties.Rows.Count == 0)
        {
            return Array.Empty<LlmCostBucket>();
        }

        List<AzureCostQueryColumn> columns = response.Properties.Columns ?? [];
        int costIndex = FindColumnIndex(columns, "Cost", "costInBillingCurrency", "PreTaxCost");
        int resourceIndex = FindColumnIndex(columns, "ResourceId");
        int currencyIndex = FindColumnIndex(columns, "Currency", "BillingCurrencyCode");

        List<LlmCostBucket> buckets = [];

        foreach (List<object?> row in response.Properties.Rows)
        {
            if (costIndex < 0 || costIndex >= row.Count)
            {
                continue;
            }

            decimal cost = ReadDecimal(row[costIndex]);
            string? resourceId = resourceIndex >= 0 && resourceIndex < row.Count ? ReadString(row[resourceIndex]) : null;
            string? currency = currencyIndex >= 0 && currencyIndex < row.Count ? ReadString(row[currencyIndex]) : null;

            decimal costUsd = string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase) ? cost : 0m;
            string? model = ExtractAccountName(resourceId);

            buckets.Add(new LlmCostBucket(
                start,
                end,
                ProviderName,
                model,
                resourceId,
                costUsd)
            {
                Tenant = _tenant.Id
            });
        }

        return buckets;
    }

    private static int FindColumnIndex(List<AzureCostQueryColumn> columns, params string[] candidates)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            string? name = columns[index].Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            foreach (string candidate in candidates)
            {
                if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static decimal ReadDecimal(object? value)
    {
        switch (value)
        {
            case null:
                return 0m;
            case decimal dec:
                return dec;
            case double dbl:
                return (decimal)dbl;
            case float flt:
                return (decimal)flt;
            case long lng:
                return lng;
            case int integer:
                return integer;
            case System.Text.Json.JsonElement element:
                return element.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.Number => element.TryGetDecimal(out decimal jsonParsed) ? jsonParsed : (decimal)element.GetDouble(),
                    System.Text.Json.JsonValueKind.String => decimal.TryParse(element.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal stringParsed) ? stringParsed : 0m,
                    _ => 0m
                };
            default:
                return decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal fallbackParsed) ? fallbackParsed : 0m;
        }
    }

    private static string? ReadString(object? value)
    {
        return value switch
        {
            null => null,
            string str => str,
            System.Text.Json.JsonElement element => element.ValueKind == System.Text.Json.JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => value.ToString()
        };
    }

    private static string? ExtractAccountName(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return null;
        }

        string[] segments = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "accounts", StringComparison.OrdinalIgnoreCase))
            {
                return segments[index + 1];
            }
        }

        return null;
    }

    private sealed class AggregatedBucket
    {
        public AggregatedBucket(DateTimeOffset start, DateTimeOffset end)
        {
            Start = start;
            End = end;
        }

        public DateTimeOffset Start { get; }

        public DateTimeOffset End { get; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long RequestCount { get; set; }
    }
}
