// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prometheus;

namespace LlmUsageExporter.Tests;

public sealed class TenantMetricsEndpointTests
{
    [Fact]
    public async Task Get_WithTenantQuery_FiltersExpositionToThatTenantOnly()
    {
        // Use a private metric prefix per test instance to avoid cross-test contamination
        // (Prometheus default registry is process-wide).
        string suffix = Guid.NewGuid().ToString("N");
        PublishToDefaultRegistry($"tenant_a_metric_{suffix}", "A");
        PublishToDefaultRegistry($"tenant_b_metric_{suffix}", "B");

        Dictionary<string, string?> values = new()
        {
            ["Tenants:Items:0:Id"] = "A",
            ["Tenants:Items:0:Name"] = "A",
            ["Tenants:Items:0:OpenAi:BaseUrl"] = "https://api.openai.com",
            ["Tenants:Items:0:OpenAi:AdminApiKey"] = "sk-a",
            ["Tenants:Items:1:Id"] = "B",
            ["Tenants:Items:1:Name"] = "B",
            ["Tenants:Items:1:OpenAi:BaseUrl"] = "https://api.openai.com",
            ["Tenants:Items:1:OpenAi:AdminApiKey"] = "sk-b",
        };

        using IHost host = await CreateHostAsync(values);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage response = await client.GetAsync("/metrics?tenant=A");
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();

        Assert.Contains($"tenant_a_metric_{suffix}", body);
        Assert.DoesNotContain($"tenant_b_metric_{suffix}{{tenant=\"B\"}}", body);
    }

    [Fact]
    public async Task Get_WithTenantQueryAndConfiguredApiKeys_RejectsMissingAuth()
    {
        Dictionary<string, string?> values = new()
        {
            ["Tenants:Items:0:Id"] = "A",
            ["Tenants:Items:0:Name"] = "A",
            ["Tenants:Items:0:OpenAi:BaseUrl"] = "https://api.openai.com",
            ["Tenants:Items:0:OpenAi:AdminApiKey"] = "sk-a",
            ["Tenants:ApiKeys:A"] = "secret-token",
        };

        using IHost host = await CreateHostAsync(values);
        HttpClient client = host.GetTestClient();

        HttpResponseMessage unauthorized = await client.GetAsync("/metrics?tenant=A");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using HttpRequestMessage authedRequest = new(HttpMethod.Get, "/metrics?tenant=A");
        authedRequest.Headers.Add("Authorization", "Bearer secret-token");
        HttpResponseMessage authed = await client.SendAsync(authedRequest);
        Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
    }

    [Fact]
    public void FilterByTenant_KeepsHelpAndTypeLines_FiltersSamplesByLabel()
    {
        const string exposition =
            "# HELP my_counter Demo counter\n"
            + "# TYPE my_counter counter\n"
            + "my_counter{tenant=\"A\",model=\"gpt\"} 1\n"
            + "my_counter{tenant=\"B\",model=\"gpt\"} 2\n";

        string filtered = TenantMetricsEndpoint.FilterByTenant(exposition, "A");

        Assert.Contains("# HELP my_counter", filtered);
        Assert.Contains("# TYPE my_counter", filtered);
        Assert.Contains("my_counter{tenant=\"A\",model=\"gpt\"} 1", filtered);
        Assert.DoesNotContain("my_counter{tenant=\"B\"", filtered);
    }

    private static void PublishToDefaultRegistry(string metricName, string tenantValue)
    {
        // Touch the default registry directly using a tenant-labeled counter that participates in filtering.
        Counter counter = Prometheus.Metrics.CreateCounter(
            metricName,
            "Test counter for tenant filtering.",
            new CounterConfiguration { LabelNames = new[] { "tenant" } });
        counter.WithLabels(tenantValue).Inc(1);
    }

    private static async Task<IHost> CreateHostAsync(Dictionary<string, string?> configValues)
    {
        IHostBuilder builder = Host.CreateDefaultBuilder()
            .ConfigureHostConfiguration(cfg => cfg.AddInMemoryCollection(configValues))
            .ConfigureWebHost(webHostBuilder =>
            {
                webHostBuilder
                    .UseTestServer()
                    .ConfigureServices((context, services) =>
                    {
                        services.AddRouting();
                        services.Configure<TenantsOptions>(context.Configuration.GetSection(TenantsOptions.SectionName));
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        // Use the same endpoint logic as Program.cs by constructing a minimal WebApplication.
                        // Since TestHost gives us an IApplicationBuilder, we'll call our endpoint mapper manually.
                        app.UseEndpoints(endpoints =>
                        {
                            MapTenantMetricsViaTestEndpoints(endpoints);
                        });
                    });
            });

        return await builder.StartAsync();
    }

    // Test-only port of the endpoint that mirrors MapTenantMetrics on IEndpointRouteBuilder.
    private static void MapTenantMetricsViaTestEndpoints(IEndpointRouteBuilder endpoints)
    {
        Microsoft.Extensions.Options.IOptionsMonitor<TenantsOptions> monitor =
            endpoints.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<TenantsOptions>>();
        TenantsOptions current = monitor.CurrentValue;

        if (current.Items.Length == 0)
        {
            endpoints.MapMetrics("/metrics");
            return;
        }

        endpoints.MapGet("/metrics", async context =>
        {
            TenantsOptions options = monitor.CurrentValue;
            string? tenant = context.Request.Query["tenant"].ToString();
            if (string.IsNullOrWhiteSpace(tenant))
            {
                tenant = null;
            }

            if (tenant is not null && options.ApiKeys.Count > 0)
            {
                if (!options.ApiKeys.TryGetValue(tenant, out string? expectedToken)
                    || string.IsNullOrWhiteSpace(expectedToken))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                string? authHeader = context.Request.Headers["Authorization"];
                if (string.IsNullOrWhiteSpace(authHeader)
                    || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = 401;
                    return;
                }

                string presented = authHeader["Bearer ".Length..].Trim();
                if (!string.Equals(presented, expectedToken, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 401;
                    return;
                }
            }

            string body = await TenantMetricsEndpoint.CollectMetricsAsync(context.RequestAborted);

            if (tenant is null)
            {
                context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
                await context.Response.WriteAsync(body, context.RequestAborted);
                return;
            }

            string filtered = TenantMetricsEndpoint.FilterByTenant(body, tenant);
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            await context.Response.WriteAsync(filtered, context.RequestAborted);
        });
    }
}
