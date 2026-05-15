// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Gemini;

public sealed class GeminiUsageProvider : ILlmUsageProvider
{
    private const string ProviderName = "gemini";

    private readonly GeminiUsageClient _client;
    private readonly IOptionsMonitor<GeminiOptions> _options;
    private readonly TenantContext _tenant;

    public GeminiUsageProvider(GeminiUsageClient client, IOptionsMonitor<GeminiOptions> options)
        : this(client, options, TenantContext.Default)
    {
    }

    public GeminiUsageProvider(GeminiUsageClient client, IOptionsMonitor<GeminiOptions> options, TenantContext tenant)
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
        GeminiOptions options = _options.CurrentValue;
        string alignmentPeriod = $"{Math.Max(options.UsageAlignmentPeriodSeconds, 60)}s";
        var query = new GeminiUsageQuery(start, end, alignmentPeriod, options.Models);

        IReadOnlyList<GeminiTimeSeries> inputSeries = await _client.GetInputTokenSeriesAsync(query, cancellationToken);
        IReadOnlyList<GeminiTimeSeries> outputSeries = await _client.GetOutputTokenSeriesAsync(query, cancellationToken);
        IReadOnlyList<GeminiTimeSeries> requestSeries = await _client.GetRequestCountSeriesAsync(query, cancellationToken);

        Dictionary<UsageKey, UsageAccumulator> accumulator = new();

        Accumulate(inputSeries, accumulator, options, (acc, value) => acc.InputTokens += value);
        Accumulate(outputSeries, accumulator, options, (acc, value) => acc.OutputTokens += value);
        Accumulate(requestSeries, accumulator, options, (acc, value) => acc.RequestCount += value);

        if (accumulator.Count == 0)
        {
            return [];
        }

        if (options.Models is { Length: > 0 })
        {
            HashSet<string> allowedModels = new(options.Models, StringComparer.OrdinalIgnoreCase);
            return accumulator
                .Where(entry => entry.Key.Model is null || allowedModels.Contains(entry.Key.Model))
                .Select(entry => BuildBucket(entry.Key, entry.Value, _tenant.Id))
                .ToArray();
        }

        return accumulator
            .Select(entry => BuildBucket(entry.Key, entry.Value, _tenant.Id))
            .ToArray();
    }

    public async Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        GeminiOptions options = _options.CurrentValue;
        if (!options.EnableCostQueries)
        {
            return [];
        }

        GeminiBigQueryResponse response = await _client.GetBillingCostsAsync(new GeminiCostsQuery(start, end), cancellationToken);

        if (response.Rows is null || response.Rows.Count == 0 || response.Schema?.Fields is null)
        {
            return [];
        }

        Dictionary<string, int> columnIndex = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < response.Schema.Fields.Count; index++)
        {
            string? name = response.Schema.Fields[index].Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                columnIndex[name!] = index;
            }
        }

        List<LlmCostBucket> buckets = [];
        foreach (GeminiBigQueryRow row in response.Rows)
        {
            if (row.Fields is null)
            {
                continue;
            }

            string? dayRaw = ReadCell(row, columnIndex, "day");
            string? sku = ReadCell(row, columnIndex, "sku");
            string? costRaw = ReadCell(row, columnIndex, "cost");
            string? currency = ReadCell(row, columnIndex, "currency");

            DateTimeOffset bucketStart = ParseBigQueryTimestamp(dayRaw) ?? start;
            DateTimeOffset bucketEnd = bucketStart.AddDays(1);

            decimal cost = 0m;
            if (decimal.TryParse(costRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsedCost)
                && string.Equals(currency, "usd", StringComparison.OrdinalIgnoreCase))
            {
                cost = parsedCost;
            }

            string? model = ExtractModelFromSku(sku);

            buckets.Add(new LlmCostBucket(
                bucketStart,
                bucketEnd,
                ProviderName,
                model,
                options.ProjectId,
                cost)
            {
                Tenant = _tenant.Id
            });
        }

        return buckets;
    }

    private static void Accumulate(
        IReadOnlyList<GeminiTimeSeries> series,
        Dictionary<UsageKey, UsageAccumulator> accumulator,
        GeminiOptions options,
        Action<UsageAccumulator, long> add)
    {
        foreach (GeminiTimeSeries entry in series)
        {
            string? model = null;
            if (entry.Metric?.Labels is { } metricLabels)
            {
                metricLabels.TryGetValue("model_id", out model);
            }

            string? projectId = null;
            if (entry.Resource?.Labels is { } resourceLabels)
            {
                resourceLabels.TryGetValue("project_id", out projectId);
            }

            if (string.IsNullOrWhiteSpace(projectId))
            {
                projectId = options.ProjectId;
            }

            if (entry.Points is null)
            {
                continue;
            }

            foreach (GeminiTimeSeriesPoint point in entry.Points)
            {
                if (point.Interval?.StartTime is null || point.Interval?.EndTime is null)
                {
                    continue;
                }

                long value = ParsePointValue(point.Value);
                if (value == 0)
                {
                    continue;
                }

                var key = new UsageKey(
                    point.Interval.StartTime.Value,
                    point.Interval.EndTime.Value,
                    model,
                    projectId);

                if (!accumulator.TryGetValue(key, out UsageAccumulator? existing))
                {
                    existing = new UsageAccumulator();
                    accumulator[key] = existing;
                }

                add(existing, value);
            }
        }
    }

    private static LlmUsageBucket BuildBucket(UsageKey key, UsageAccumulator value, string tenantId)
    {
        return new LlmUsageBucket(
            key.Start,
            key.End,
            ProviderName,
            key.Model,
            key.ProjectId,
            value.InputTokens,
            value.OutputTokens,
            0,
            value.InputTokens + value.OutputTokens,
            value.RequestCount)
        {
            Tenant = tenantId
        };
    }

    private static long ParsePointValue(GeminiPointValue? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (!string.IsNullOrWhiteSpace(value.Int64Value)
            && long.TryParse(value.Int64Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            return parsed;
        }

        if (value.DoubleValue is double doubleValue)
        {
            return (long)Math.Round(doubleValue);
        }

        return 0;
    }

    private static string? ExtractModelFromSku(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return null;
        }

        int spaceIndex = sku.IndexOf(' ');
        return spaceIndex > 0 ? sku[..spaceIndex] : sku;
    }

    private static string? ReadCell(
        GeminiBigQueryRow row,
        IReadOnlyDictionary<string, int> columnIndex,
        string column)
    {
        if (!columnIndex.TryGetValue(column, out int index) || row.Fields is null || index >= row.Fields.Count)
        {
            return null;
        }

        object? raw = row.Fields[index].Value;
        return raw switch
        {
            null => null,
            string s => s,
            JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString(),
            _ => raw.ToString()
        };
    }

    private static DateTimeOffset? ParseBigQueryTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return parsed;
        }

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000));
        }

        return null;
    }

    private readonly record struct UsageKey(
        DateTimeOffset Start,
        DateTimeOffset End,
        string? Model,
        string? ProjectId);

    private sealed class UsageAccumulator
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long RequestCount { get; set; }
    }
}
