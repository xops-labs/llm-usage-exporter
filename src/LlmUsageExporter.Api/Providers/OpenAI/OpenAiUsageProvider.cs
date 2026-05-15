// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.OpenAI;

public sealed class OpenAiUsageProvider : ILlmUsageProvider
{
    private const string ProviderName = "openai";
    private static readonly string[] CostGroupBy = ["project_id", "line_item"];

    private readonly OpenAiUsageClient _client;
    private readonly OpenAiOptions _options;
    private readonly TenantContext _tenant;

    public OpenAiUsageProvider(OpenAiUsageClient client, IOptions<OpenAiOptions> options)
        : this(client, options.Value, TenantContext.Default)
    {
    }

    public OpenAiUsageProvider(OpenAiUsageClient client, OpenAiOptions options, TenantContext tenant)
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
        var query = new OpenAiUsageQuery(
            start,
            end,
            _options.UsageBucketWidth,
            _options.GroupBy,
            _options.ProjectIds,
            _options.Models,
            _options.ApiKeyIds,
            _options.UserIds,
            _options.UsagePageLimit);

        IReadOnlyList<OpenAiUsageBucketDto> buckets = await _client.GetCompletionsUsageAsync(query, cancellationToken);
        return buckets.SelectMany(bucket => MapUsageBucket(bucket, _tenant.Id)).ToArray();
    }

    public async Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var query = new OpenAiCostsQuery(
            start,
            end,
            _options.CostsBucketWidth,
            CostGroupBy,
            _options.ProjectIds,
            _options.ApiKeyIds,
            _options.CostsPageLimit);

        IReadOnlyList<OpenAiCostBucketDto> buckets = await _client.GetCostsAsync(query, cancellationToken);
        return buckets.SelectMany(bucket => MapCostBucket(bucket, _tenant.Id)).ToArray();
    }

    private static IEnumerable<LlmUsageBucket> MapUsageBucket(OpenAiUsageBucketDto bucket, string tenantId)
    {
        if (bucket.StartTime is null || bucket.EndTime is null)
        {
            yield break;
        }

        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(bucket.StartTime.Value);
        DateTimeOffset end = DateTimeOffset.FromUnixTimeSeconds(bucket.EndTime.Value);

        foreach (OpenAiUsageResultDto result in bucket.Results ?? [])
        {
            long inputTokens = result.InputTokens ?? 0;
            long outputTokens = result.OutputTokens ?? 0;

            yield return new LlmUsageBucket(
                start,
                end,
                ProviderName,
                result.Model,
                result.ProjectId,
                inputTokens,
                outputTokens,
                result.InputCachedTokens ?? 0,
                inputTokens + outputTokens,
                result.NumModelRequests ?? 0)
            {
                Tenant = tenantId
            };
        }
    }

    private static IEnumerable<LlmCostBucket> MapCostBucket(OpenAiCostBucketDto bucket, string tenantId)
    {
        if (bucket.StartTime is null || bucket.EndTime is null)
        {
            yield break;
        }

        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(bucket.StartTime.Value);
        DateTimeOffset end = DateTimeOffset.FromUnixTimeSeconds(bucket.EndTime.Value);

        foreach (OpenAiCostResultDto result in bucket.Results ?? [])
        {
            string? model = result.Model ?? result.LineItem;
            decimal costUsd = string.Equals(result.Amount?.Currency, "usd", StringComparison.OrdinalIgnoreCase)
                ? result.Amount?.Value ?? 0m
                : 0m;

            yield return new LlmCostBucket(
                start,
                end,
                ProviderName,
                model,
                result.ProjectId,
                costUsd)
            {
                Tenant = tenantId
            };
        }
    }
}
