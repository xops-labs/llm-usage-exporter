// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Anthropic;

public sealed class AnthropicUsageProvider : ILlmUsageProvider
{
    private const string ProviderName = "anthropic";
    private static readonly string[] CostGroupBy = ["workspace_id", "description"];

    private readonly AnthropicUsageClient _client;
    private readonly AnthropicOptions _options;
    private readonly TenantContext _tenant;

    public AnthropicUsageProvider(AnthropicUsageClient client, IOptions<AnthropicOptions> options)
        : this(client, options.Value, TenantContext.Default)
    {
    }

    public AnthropicUsageProvider(AnthropicUsageClient client, AnthropicOptions options, TenantContext tenant)
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
        var query = new AnthropicUsageQuery(
            start,
            end,
            _options.UsageBucketWidth,
            _options.GroupBy,
            _options.WorkspaceIds,
            _options.Models,
            _options.ApiKeyIds,
            _options.UsagePageLimit);

        IReadOnlyList<AnthropicUsageBucketDto> buckets = await _client.GetMessagesUsageAsync(query, cancellationToken);
        return buckets.SelectMany(bucket => MapUsageBucket(bucket, _tenant.Id)).ToArray();
    }

    public async Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var query = new AnthropicCostsQuery(
            start,
            end,
            _options.CostsBucketWidth,
            CostGroupBy,
            _options.WorkspaceIds,
            _options.CostsPageLimit);

        IReadOnlyList<AnthropicCostBucketDto> buckets = await _client.GetCostsAsync(query, cancellationToken);
        return buckets.SelectMany(bucket => MapCostBucket(bucket, _tenant.Id)).ToArray();
    }

    private static IEnumerable<LlmUsageBucket> MapUsageBucket(AnthropicUsageBucketDto bucket, string tenantId)
    {
        if (!TryParseTimestamp(bucket.StartingAt, out DateTimeOffset start)
            || !TryParseTimestamp(bucket.EndingAt, out DateTimeOffset end))
        {
            yield break;
        }

        foreach (AnthropicUsageResultDto result in bucket.Results ?? [])
        {
            long uncachedInputTokens = result.UncachedInputTokens ?? 0;
            long cacheCreationTokens = result.CacheCreationInputTokens ?? 0;
            long inputTokens = uncachedInputTokens + cacheCreationTokens;
            long outputTokens = result.OutputTokens ?? 0;

            yield return new LlmUsageBucket(
                start,
                end,
                ProviderName,
                result.Model,
                result.WorkspaceId,
                inputTokens,
                outputTokens,
                result.CachedInputTokens ?? 0,
                inputTokens + outputTokens,
                0)
            {
                Tenant = tenantId
            };
        }
    }

    private static IEnumerable<LlmCostBucket> MapCostBucket(AnthropicCostBucketDto bucket, string tenantId)
    {
        if (!TryParseTimestamp(bucket.StartingAt, out DateTimeOffset start)
            || !TryParseTimestamp(bucket.EndingAt, out DateTimeOffset end))
        {
            yield break;
        }

        foreach (AnthropicCostResultDto result in bucket.Results ?? [])
        {
            string? model = ParseModelFromDescription(result.Description);
            decimal costUsd = ParseAmountUsd(result.Amount);

            yield return new LlmCostBucket(
                start,
                end,
                ProviderName,
                model,
                result.WorkspaceId,
                costUsd)
            {
                Tenant = tenantId
            };
        }
    }

    private static bool TryParseTimestamp(string? value, out DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            timestamp = default;
            return false;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out timestamp);
    }

    private static string? ParseModelFromDescription(string? description)
    {
        if (description is null)
        {
            return null;
        }

        int spaceIndex = description.IndexOf(' ');
        return spaceIndex > 0 ? description[..spaceIndex] : description;
    }

    private static decimal ParseAmountUsd(AnthropicAmountDto? amount)
    {
        if (amount is null)
        {
            return 0m;
        }

        if (!string.Equals(amount.Currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return 0m;
        }

        if (string.IsNullOrWhiteSpace(amount.Amount))
        {
            return 0m;
        }

        return decimal.TryParse(amount.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : 0m;
    }
}
