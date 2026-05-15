// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using LlmUsageExporter.Api.Focus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LlmUsageExporter.Tests;

public sealed class FocusEndpointTests
{
    [Fact]
    public async Task MapFocusJson_ReturnsRecordsAsJsonArray()
    {
        var store = new InMemoryFocusRecordStore(capacity: 16);
        store.Append(new[]
        {
            MakeRecord("gpt-4o", "OpenAI", "OpenAI API", 1.25m),
            MakeRecord("claude-3-5-sonnet", "Anthropic", "Anthropic Messages API", 2.5m)
        });

        using IHost host = await CreateHostAsync(store);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/focus.json");
        response.EnsureSuccessStatusCode();
        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        JsonElement[] elements = root.EnumerateArray().ToArray();
        Assert.Equal(2, elements.Length);

        string[] resourceNames = elements
            .Select(element => element.GetProperty("resourceName").GetString() ?? string.Empty)
            .ToArray();
        Assert.Contains("gpt-4o", resourceNames);
        Assert.Contains("claude-3-5-sonnet", resourceNames);

        JsonElement first = elements[0];
        Assert.Equal("OpenAI", first.GetProperty("providerName").GetString());
        Assert.Equal("OpenAI API", first.GetProperty("serviceName").GetString());
        Assert.Equal("AI and Machine Learning", first.GetProperty("serviceCategory").GetString());
        Assert.Equal("Generative AI", first.GetProperty("serviceSubcategory").GetString());
        Assert.Equal("Token", first.GetProperty("pricingUnit").GetString());
        Assert.Equal(1.25m, first.GetProperty("billedCost").GetDecimal());
    }

    [Fact]
    public async Task MapFocusCsv_ReturnsValidCsvWithHeaderRow()
    {
        var store = new InMemoryFocusRecordStore(capacity: 16);
        store.Append(new[]
        {
            MakeRecord("gpt-4o", "OpenAI", "OpenAI API", 1.25m),
            MakeRecord("model,with,commas", "OpenAI", "OpenAI API", 2.5m, description: "Has \"quotes\" and, commas")
        });

        using IHost host = await CreateHostAsync(store);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/focus.csv");
        response.EnsureSuccessStatusCode();
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType?.MediaType);

        string body = await response.Content.ReadAsStringAsync();
        string[] lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.True(lines.Length >= 3);

        string header = lines[0];
        Assert.StartsWith("ChargePeriodStart,ChargePeriodEnd,BillingAccountId,Tenant,BillingAccountName", header);
        Assert.Contains("Tags", header);
        Assert.EndsWith("x_tenant_id", header);

        string dataWithCommas = lines[2];
        Assert.Contains("\"model,with,commas\"", dataWithCommas);
        Assert.Contains("\"Has \"\"quotes\"\" and, commas\"", dataWithCommas);
    }

    // Column-order contract: any change here is a breaking schema change for downstream
    // warehouse COPY statements and FinOps tools with fixed schemas. Add new FOCUS columns
    // only under a versioned endpoint (e.g. /focus-1.3.csv), never by appending here silently.
    private static readonly string[] ExpectedColumns =
    [
        "ChargePeriodStart", "ChargePeriodEnd",
        "BillingAccountId", "Tenant", "BillingAccountName",
        "SubAccountId", "SubAccountName",
        "ProviderName", "PublisherName", "InvoiceIssuerName",
        "ServiceName", "ServiceCategory", "ServiceSubcategory",
        "ResourceId", "ResourceName", "ResourceType",
        "ChargeCategory", "ChargeClass", "ChargeDescription", "ChargeFrequency",
        "BilledCost", "EffectiveCost", "ListCost", "ContractedCost",
        "BillingCurrency", "PricingCurrency", "PricingCategory", "PricingUnit",
        "ConsumedQuantity", "ConsumedUnit",
        "Region", "Tags",
        "x_provider_native_id", "x_tenant_id"
    ];

    [Fact]
    public async Task MapFocusCsv_ColumnOrderIsSchemaContract()
    {
        var store = new InMemoryFocusRecordStore(capacity: 16);
        store.Append(new[] { MakeRecord("gpt-4o", "OpenAI", "OpenAI API", 1.0m) });

        using IHost host = await CreateHostAsync(store);
        string body = await host.GetTestClient().GetStringAsync("/focus.csv");

        string headerRow = body.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Equal(string.Join(',', ExpectedColumns), headerRow);
    }

    [Fact]
    public async Task MapFocusCsv_IncludesFocusVersionHeader()
    {
        var store = new InMemoryFocusRecordStore(capacity: 16);
        using IHost host = await CreateHostAsync(store);

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/focus.csv");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("X-FOCUS-Version"),
            "Response must include X-FOCUS-Version header so consumers can detect the schema.");
        Assert.Equal(FocusEndpoint.FocusVersion,
            response.Headers.GetValues("X-FOCUS-Version").Single());
    }

    [Fact]
    public async Task MapFocusJson_IncludesFocusVersionHeader()
    {
        var store = new InMemoryFocusRecordStore(capacity: 16);
        using IHost host = await CreateHostAsync(store);

        HttpResponseMessage response = await host.GetTestClient().GetAsync("/focus.json");
        response.EnsureSuccessStatusCode();
        Assert.True(response.Headers.Contains("X-FOCUS-Version"),
            "Response must include X-FOCUS-Version header so consumers can detect the schema.");
        Assert.Equal(FocusEndpoint.FocusVersion,
            response.Headers.GetValues("X-FOCUS-Version").Single());
    }

    private static async Task<IHost> CreateHostAsync(IFocusRecordStore store)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder()
            .ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        services.AddSingleton(store);
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapFocusCsv();
                            endpoints.MapFocusJson();
                        });
                    });
            });

        return await builder.StartAsync();
    }

    private static FocusRecord MakeRecord(string model, string provider, string service, decimal cost, string? description = null)
    {
        return new FocusRecord
        {
            ChargePeriodStart = "2026-01-01T00:00:00+00:00",
            ChargePeriodEnd = "2026-01-02T00:00:00+00:00",
            BillingAccountId = "acct",
            BillingAccountName = "acct",
            ProviderName = provider,
            PublisherName = provider,
            InvoiceIssuerName = provider,
            ServiceName = service,
            ResourceId = $"{provider}:{model}:acct",
            ResourceName = model,
            ChargeDescription = description ?? $"LLM token usage for model {model} on provider {provider}",
            BilledCost = cost,
            EffectiveCost = cost,
            ListCost = cost,
            ContractedCost = cost,
            Tags = "{\"llm_provider\":\"" + provider.ToLowerInvariant() + "\",\"llm_model\":\"" + model + "\"}",
            x_provider_native_id = "acct"
        };
    }
}
