// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Prometheus;

namespace LlmUsageExporter.Api.Endpoints;

public static class TenantMetricsEndpoint
{
    private const string PrometheusContentType = "text/plain; version=0.0.4; charset=utf-8";

    public static IEndpointConventionBuilder MapTenantMetrics(this WebApplication app)
    {
        IOptionsMonitor<TenantsOptions> optionsMonitor = app.Services.GetRequiredService<IOptionsMonitor<TenantsOptions>>();
        TenantsOptions current = optionsMonitor.CurrentValue;

        if (current.Items.Length == 0)
        {
            // Single-tenant backward compat: serve the default registry via the prometheus-net helper.
            return app.MapMetrics("/metrics");
        }

        return app.MapGet("/metrics", async (HttpContext context, IOptionsMonitor<TenantsOptions> monitor) =>
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
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                string? authHeader = context.Request.Headers["Authorization"];
                if (string.IsNullOrWhiteSpace(authHeader)
                    || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                string presented = authHeader["Bearer ".Length..].Trim();
                // FixedTimeEquals prevents timing-oracle attacks against the bearer token.
                if (!CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(presented),
                        Encoding.UTF8.GetBytes(expectedToken)))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            string body = await CollectMetricsAsync(context.RequestAborted);

            if (tenant is null)
            {
                context.Response.ContentType = PrometheusContentType;
                await context.Response.WriteAsync(body, context.RequestAborted);
                return;
            }

            string filtered = FilterByTenant(body, tenant);
            context.Response.ContentType = PrometheusContentType;
            await context.Response.WriteAsync(filtered, context.RequestAborted);
        });
    }

    public static async Task<string> CollectMetricsAsync(CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream, cancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string FilterByTenant(string exposition, string tenant)
    {
        if (string.IsNullOrEmpty(exposition))
        {
            return exposition;
        }

        string needle = $"tenant=\"{tenant}\"";
        StringBuilder builder = new(exposition.Length);

        foreach (string line in exposition.Split('\n'))
        {
            if (line.Length == 0)
            {
                builder.Append('\n');
                continue;
            }

            if (line[0] == '#')
            {
                builder.Append(line);
                builder.Append('\n');
                continue;
            }

            // Sample line: keep only if it carries the matching tenant label.
            if (line.Contains(needle, StringComparison.Ordinal))
            {
                builder.Append(line);
                builder.Append('\n');
            }
        }

        return builder.ToString();
    }
}
