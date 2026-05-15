// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Focus;
using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Tests;

public sealed class FocusRecordMapperTests
{
    [Fact]
    public void MapCostBuckets_PopulatesEveryRequiredColumn()
    {
        var buckets = new[]
        {
            MakeBucket("openai", "gpt-4o", "proj_abc", 1.23m),
            MakeBucket("azure_openai", "gpt-4o-mini", "/subs/contoso", 4.56m),
            MakeBucket("anthropic", "claude-3-5-sonnet", "ws_42", 7.89m),
            MakeBucket("gemini", "gemini-1.5-pro", "my-gcp-proj", 10.11m),
            MakeBucket("bedrock", "anthropic.claude-3-5-sonnet-20240620-v1:0", "us-east-1", 12.13m)
        };

        FocusRecord[] records = FocusRecordMapper.MapCostBuckets(buckets).ToArray();

        Assert.Equal(5, records.Length);

        var openai = records[0];
        Assert.Equal("OpenAI", openai.ProviderName);
        Assert.Equal("OpenAI", openai.PublisherName);
        Assert.Equal("OpenAI", openai.InvoiceIssuerName);
        Assert.Equal("OpenAI API", openai.ServiceName);
        Assert.Equal("AI and Machine Learning", openai.ServiceCategory);
        Assert.Equal("Generative AI", openai.ServiceSubcategory);
        Assert.Equal("LLM Model", openai.ResourceType);
        Assert.Equal("Usage", openai.ChargeCategory);
        Assert.Equal("Usage-Based", openai.ChargeFrequency);
        Assert.Equal("USD", openai.BillingCurrency);
        Assert.Equal("USD", openai.PricingCurrency);
        Assert.Equal("Standard", openai.PricingCategory);
        Assert.Equal("Token", openai.PricingUnit);
        Assert.Equal("Token", openai.ConsumedUnit);
        Assert.Equal(string.Empty, openai.ConsumedQuantity);
        Assert.Equal(1.23m, openai.BilledCost);
        Assert.Equal(1.23m, openai.EffectiveCost);
        Assert.Equal(1.23m, openai.ListCost);
        Assert.Equal(1.23m, openai.ContractedCost);
        Assert.Equal("openai:gpt-4o:proj_abc", openai.ResourceId);
        Assert.Equal("gpt-4o", openai.ResourceName);
        Assert.Equal("proj_abc", openai.BillingAccountId);
        Assert.Equal("proj_abc", openai.BillingAccountName);
        Assert.Equal(string.Empty, openai.SubAccountId);
        Assert.Equal(string.Empty, openai.SubAccountName);
        Assert.Equal(string.Empty, openai.Region);
        Assert.Equal("proj_abc", openai.x_provider_native_id);
        Assert.Contains("LLM token usage for model gpt-4o on provider openai", openai.ChargeDescription);

        Assert.Equal("Microsoft Azure", records[1].ProviderName);
        Assert.Equal("Azure OpenAI", records[1].ServiceName);

        Assert.Equal("Anthropic", records[2].ProviderName);
        Assert.Equal("Anthropic Messages API", records[2].ServiceName);

        Assert.Equal("Google Cloud", records[3].ProviderName);
        Assert.Equal("Vertex AI", records[3].ServiceName);

        Assert.Equal("Amazon Web Services", records[4].ProviderName);
        Assert.Equal("Amazon Bedrock", records[4].ServiceName);
        Assert.Equal("us-east-1", records[4].Region);
    }

    [Fact]
    public void MapCostBuckets_FillsTagsWithProviderAndModel()
    {
        FocusRecord record = FocusRecordMapper.MapCostBucket(
            MakeBucket("openai", "gpt-4o", "proj_abc", 2.5m));

        Assert.Equal("{\"llm_provider\":\"openai\",\"llm_model\":\"gpt-4o\"}", record.Tags);
    }

    [Fact]
    public void MapCostBuckets_TreatsMissingModelAsUnknown()
    {
        FocusRecord nullModel = FocusRecordMapper.MapCostBucket(
            MakeBucket("openai", null, "proj_abc", 0.5m));
        FocusRecord emptyModel = FocusRecordMapper.MapCostBucket(
            MakeBucket("openai", "   ", "proj_abc", 0.5m));

        Assert.Equal("unknown", nullModel.ResourceName);
        Assert.Equal("openai:unknown:proj_abc", nullModel.ResourceId);
        Assert.Contains("model unknown", nullModel.ChargeDescription);
        Assert.Contains("\"llm_model\":\"unknown\"", nullModel.Tags);

        Assert.Equal("unknown", emptyModel.ResourceName);
        Assert.Equal("openai:unknown:proj_abc", emptyModel.ResourceId);
    }

    private static LlmCostBucket MakeBucket(string provider, string? model, string projectId, decimal cost)
    {
        return new LlmCostBucket(
            DateTimeOffset.FromUnixTimeSeconds(1_710_000_000),
            DateTimeOffset.FromUnixTimeSeconds(1_710_086_400),
            provider,
            model,
            projectId,
            cost);
    }
}
