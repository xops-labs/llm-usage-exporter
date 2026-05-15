// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Focus;

public sealed record FocusRecord
{
    public required string ChargePeriodStart { get; init; }
    public required string ChargePeriodEnd { get; init; }
    public required string BillingAccountId { get; init; }
    public string Tenant { get; init; } = "default";
    public required string BillingAccountName { get; init; }
    public string SubAccountId { get; init; } = string.Empty;
    public string SubAccountName { get; init; } = string.Empty;
    public required string ProviderName { get; init; }
    public required string PublisherName { get; init; }
    public required string InvoiceIssuerName { get; init; }
    public required string ServiceName { get; init; }
    public string ServiceCategory { get; init; } = "AI and Machine Learning";
    public string ServiceSubcategory { get; init; } = "Generative AI";
    public required string ResourceId { get; init; }
    public required string ResourceName { get; init; }
    public string ResourceType { get; init; } = "LLM Model";
    public string ChargeCategory { get; init; } = "Usage";
    public string ChargeClass { get; init; } = string.Empty;
    public required string ChargeDescription { get; init; }
    public string ChargeFrequency { get; init; } = "Usage-Based";
    public required decimal BilledCost { get; init; }
    public required decimal EffectiveCost { get; init; }
    public required decimal ListCost { get; init; }
    public required decimal ContractedCost { get; init; }
    public string BillingCurrency { get; init; } = "USD";
    public string PricingCurrency { get; init; } = "USD";
    public string PricingCategory { get; init; } = "Standard";
    public string PricingUnit { get; init; } = "Token";
    public string ConsumedQuantity { get; init; } = string.Empty;
    public string ConsumedUnit { get; init; } = "Token";
    public string Region { get; init; } = string.Empty;
    public required string Tags { get; init; }
    public required string x_provider_native_id { get; init; }
    public string x_tenant_id { get; init; } = "default";
}
