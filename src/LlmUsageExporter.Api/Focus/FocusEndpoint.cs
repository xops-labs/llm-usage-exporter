// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmUsageExporter.Api.Focus;

public static class FocusEndpoint
{
    private static readonly string[] ColumnOrder =
    [
        "ChargePeriodStart",
        "ChargePeriodEnd",
        "BillingAccountId",
        "Tenant",
        "BillingAccountName",
        "SubAccountId",
        "SubAccountName",
        "ProviderName",
        "PublisherName",
        "InvoiceIssuerName",
        "ServiceName",
        "ServiceCategory",
        "ServiceSubcategory",
        "ResourceId",
        "ResourceName",
        "ResourceType",
        "ChargeCategory",
        "ChargeClass",
        "ChargeDescription",
        "ChargeFrequency",
        "BilledCost",
        "EffectiveCost",
        "ListCost",
        "ContractedCost",
        "BillingCurrency",
        "PricingCurrency",
        "PricingCategory",
        "PricingUnit",
        "ConsumedQuantity",
        "ConsumedUnit",
        "Region",
        "Tags",
        "x_provider_native_id",
        "x_tenant_id"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false
    };

    // The FOCUS version emitted by this exporter. Surfaced as an HTTP response header
    // on both endpoints so consumers can detect the schema version without inspecting columns.
    public const string FocusVersion = "1.0";

    public static IEndpointConventionBuilder MapFocusCsv(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/focus.csv", (IFocusRecordStore store, HttpContext context) =>
        {
            context.Response.Headers["X-FOCUS-Version"] = FocusVersion;
            IReadOnlyList<FocusRecord> records = store.Snapshot();
            string csv = WriteCsv(records);
            byte[] bytes = Encoding.UTF8.GetBytes(csv);
            return Results.File(
                bytes,
                contentType: "text/csv; charset=utf-8",
                fileDownloadName: "focus.csv");
        });
    }

    public static IEndpointConventionBuilder MapFocusJson(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/focus.json", (IFocusRecordStore store, HttpContext context) =>
        {
            context.Response.Headers["X-FOCUS-Version"] = FocusVersion;
            IReadOnlyList<FocusRecord> records = store.Snapshot();
            return Results.Json(records, JsonOptions);
        });
    }

    public static string WriteCsv(IReadOnlyList<FocusRecord> records)
    {
        StringBuilder builder = new();
        builder.Append(string.Join(',', ColumnOrder.Select(EscapeCsv)));
        builder.Append('\n');

        foreach (FocusRecord record in records)
        {
            string[] cells = new string[ColumnOrder.Length];
            for (int i = 0; i < ColumnOrder.Length; i++)
            {
                cells[i] = EscapeCsv(GetValue(record, ColumnOrder[i]));
            }

            builder.Append(string.Join(',', cells));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static string GetValue(FocusRecord record, string column) => column switch
    {
        "ChargePeriodStart" => record.ChargePeriodStart,
        "ChargePeriodEnd" => record.ChargePeriodEnd,
        "BillingAccountId" => record.BillingAccountId,
        "Tenant" => record.Tenant,
        "BillingAccountName" => record.BillingAccountName,
        "SubAccountId" => record.SubAccountId,
        "SubAccountName" => record.SubAccountName,
        "ProviderName" => record.ProviderName,
        "PublisherName" => record.PublisherName,
        "InvoiceIssuerName" => record.InvoiceIssuerName,
        "ServiceName" => record.ServiceName,
        "ServiceCategory" => record.ServiceCategory,
        "ServiceSubcategory" => record.ServiceSubcategory,
        "ResourceId" => record.ResourceId,
        "ResourceName" => record.ResourceName,
        "ResourceType" => record.ResourceType,
        "ChargeCategory" => record.ChargeCategory,
        "ChargeClass" => record.ChargeClass,
        "ChargeDescription" => record.ChargeDescription,
        "ChargeFrequency" => record.ChargeFrequency,
        "BilledCost" => record.BilledCost.ToString(CultureInfo.InvariantCulture),
        "EffectiveCost" => record.EffectiveCost.ToString(CultureInfo.InvariantCulture),
        "ListCost" => record.ListCost.ToString(CultureInfo.InvariantCulture),
        "ContractedCost" => record.ContractedCost.ToString(CultureInfo.InvariantCulture),
        "BillingCurrency" => record.BillingCurrency,
        "PricingCurrency" => record.PricingCurrency,
        "PricingCategory" => record.PricingCategory,
        "PricingUnit" => record.PricingUnit,
        "ConsumedQuantity" => record.ConsumedQuantity,
        "ConsumedUnit" => record.ConsumedUnit,
        "Region" => record.Region,
        "Tags" => record.Tags,
        "x_provider_native_id" => record.x_provider_native_id,
        "x_tenant_id" => record.x_tenant_id,
        _ => string.Empty
    };

    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        bool mustQuote = value.IndexOfAny(['"', ',', '\n', '\r']) >= 0;
        if (!mustQuote)
        {
            return value;
        }

        return string.Concat("\"", value.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");
    }
}
