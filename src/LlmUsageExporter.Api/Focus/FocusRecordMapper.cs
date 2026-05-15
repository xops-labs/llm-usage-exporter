// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Focus;

public static class FocusRecordMapper
{
    private const string UnknownModel = "unknown";

    public static IEnumerable<FocusRecord> MapCostBuckets(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        foreach (LlmCostBucket bucket in buckets)
        {
            yield return MapCostBucket(bucket);
        }
    }

    public static FocusRecord MapCostBucket(LlmCostBucket bucket)
    {
        string provider = bucket.Provider ?? string.Empty;
        string model = string.IsNullOrWhiteSpace(bucket.Model) ? UnknownModel : bucket.Model!;
        string projectId = bucket.TenancyId ?? string.Empty;
        string tenant = string.IsNullOrWhiteSpace(bucket.Tenant) ? "default" : bucket.Tenant;

        string providerName = MapProviderName(provider);
        string serviceName = MapServiceName(provider);
        string region = string.Equals(provider, "bedrock", StringComparison.OrdinalIgnoreCase)
            ? projectId
            : string.Empty;

        string resourceId = string.Format(
            CultureInfo.InvariantCulture,
            "{0}:{1}:{2}",
            provider,
            model,
            projectId);

        string chargeDescription = string.Format(
            CultureInfo.InvariantCulture,
            "LLM token usage for model {0} on provider {1}",
            model,
            provider);

        string tags = BuildTagsJson(provider, model);

        return new FocusRecord
        {
            ChargePeriodStart = bucket.StartTime.ToString("o", CultureInfo.InvariantCulture),
            ChargePeriodEnd = bucket.EndTime.ToString("o", CultureInfo.InvariantCulture),
            BillingAccountId = projectId,
            Tenant = tenant,
            BillingAccountName = projectId,
            ProviderName = providerName,
            PublisherName = providerName,
            InvoiceIssuerName = providerName,
            ServiceName = serviceName,
            ResourceId = resourceId,
            ResourceName = model,
            ChargeDescription = chargeDescription,
            BilledCost = bucket.CostUsd,
            EffectiveCost = bucket.CostUsd,
            ListCost = bucket.CostUsd,
            ContractedCost = bucket.CostUsd,
            Region = region,
            Tags = tags,
            x_provider_native_id = projectId,
            x_tenant_id = tenant
        };
    }

    public static string MapProviderName(string provider) => provider?.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "azure_openai" => "Microsoft Azure",
        "anthropic" => "Anthropic",
        "gemini" => "Google Cloud",
        "bedrock" => "Amazon Web Services",
        _ => provider ?? string.Empty
    };

    public static string MapServiceName(string provider) => provider?.ToLowerInvariant() switch
    {
        "openai" => "OpenAI API",
        "azure_openai" => "Azure OpenAI",
        "anthropic" => "Anthropic Messages API",
        "gemini" => "Vertex AI",
        "bedrock" => "Amazon Bedrock",
        _ => provider ?? string.Empty
    };

    private static string BuildTagsJson(string provider, string model)
    {
        return string.Concat(
            "{\"llm_provider\":\"",
            EscapeJsonString(provider),
            "\",\"llm_model\":\"",
            EscapeJsonString(model),
            "\"}");
    }

    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
