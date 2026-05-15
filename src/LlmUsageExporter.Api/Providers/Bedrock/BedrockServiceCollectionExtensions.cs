// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Bedrock;

public static class BedrockServiceCollectionExtensions
{
    private const string ProviderName = "bedrock";
    private const string HttpClientName = "bedrock";

    public static IServiceCollection AddBedrockProvider(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TenantsOptions>(configuration.GetSection(TenantsOptions.SectionName));

        TenantsOptions tenantsSnapshot = new();
        configuration.GetSection(TenantsOptions.SectionName).Bind(tenantsSnapshot);

        if (tenantsSnapshot.Items.Length > 0)
        {
            RegisterMultiTenant(services, tenantsSnapshot);
            return services;
        }

        // Single-tenant opt-in check: skip registration entirely when the operator
        // has not configured Bedrock. Without this guard the ValidateOnStart()
        // chain below crashes deployments that only want one of the other providers.
        if (!IsProviderConfigured(configuration))
        {
            return services;
        }

        services.Configure<BedrockOptions>(configuration.GetSection(BedrockOptions.SectionName));
        services.PostConfigure<BedrockOptions>(options => ApplyEnvironment(options, configuration));
        services
            .AddOptions<BedrockOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.AccessKeyId), "Bedrock:AccessKeyId (AWS_ACCESS_KEY_ID) is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.SecretAccessKey), "Bedrock:SecretAccessKey (AWS_SECRET_ACCESS_KEY) is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Region), "Bedrock:Region is required.")
            .Validate(options => options.MaxRetries >= 0, "Bedrock:MaxRetries must be zero or greater.")
            .ValidateOnStart();
        services.AddSingleton<BedrockUsageProvider>();
        services.AddHttpClient<BedrockUsageClient>(httpClient =>
        {
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        }).AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
        {
            LlmMetricsPublisher prometheus = new("bedrock", Prometheus.Metrics.DefaultFactory, serviceProvider.GetRequiredService<ICheckpointStore>(), TenantContext.Default);
            LlmMeterPublisher? otel = serviceProvider.GetService<LlmMeterPublisher>();
            ILlmMetricsPublisher publisher = otel is null
                ? prometheus
                : new CompositeMetricsPublisher(
                    new ILlmMetricsPublisher[] { prometheus, otel },
                    serviceProvider.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

            return new LlmProviderRegistration(
                ProviderName,
                serviceProvider.GetRequiredService<BedrockUsageProvider>(),
                publisher);
        });

        return services;
    }

    private static void RegisterMultiTenant(IServiceCollection services, TenantsOptions tenantsSnapshot)
    {
        services.AddHttpClient(HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
            .AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        foreach (TenantConfig tenantConfig in tenantsSnapshot.Items)
        {
            if (tenantConfig.Bedrock is null || string.IsNullOrWhiteSpace(tenantConfig.Id))
            {
                continue;
            }

            TenantConfig captured = tenantConfig;
            BedrockOptions providerOptions = captured.Bedrock!;
            TenantContext tenant = new(captured.Id, string.IsNullOrWhiteSpace(captured.Name) ? captured.Id : captured.Name);

            services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
            {
                IHttpClientFactory factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                HttpClient httpClient = factory.CreateClient(HttpClientName);

                IOptions<BedrockOptions> wrappedOptions = Options.Create(providerOptions);
                ILogger<BedrockUsageClient> clientLogger = serviceProvider.GetRequiredService<ILogger<BedrockUsageClient>>();
                BedrockUsageClient client = new(httpClient, wrappedOptions, clientLogger);
                BedrockUsageProvider provider = new(client, providerOptions, tenant);

                ICheckpointStore checkpointStore = serviceProvider.GetRequiredService<ICheckpointStore>();
                LlmMetricsPublisher prometheus = new("bedrock", Prometheus.Metrics.DefaultFactory, checkpointStore, tenant);
                LlmMeterPublisher? otel = serviceProvider.GetService<LlmMeterPublisher>();
                ILlmMetricsPublisher publisher = otel is null
                    ? prometheus
                    : new CompositeMetricsPublisher(
                        new ILlmMetricsPublisher[] { prometheus, otel },
                        serviceProvider.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

                return new LlmProviderRegistration(ProviderName, provider, publisher)
                {
                    Tenant = tenant
                };
            });
        }
    }

    /// <summary>
    /// Detects whether the operator has opted into the Bedrock provider in single-tenant mode.
    /// AWS access key id is the sentinel — required by every SigV4-signed CloudWatch / Cost Explorer call.
    /// </summary>
    private static bool IsProviderConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["AWS_ACCESS_KEY_ID"])
            || !string.IsNullOrWhiteSpace(configuration[$"{BedrockOptions.SectionName}:AccessKeyId"]);
    }

    private static void ApplyEnvironment(BedrockOptions options, IConfiguration configuration)
    {
        options.AccessKeyId = ReadString(configuration, "AWS_ACCESS_KEY_ID", options.AccessKeyId);
        options.SecretAccessKey = ReadString(configuration, "AWS_SECRET_ACCESS_KEY", options.SecretAccessKey);
        options.SessionToken = ReadOptionalString(configuration, "AWS_SESSION_TOKEN", options.SessionToken);

        string region = ReadString(configuration, "BEDROCK_REGION", options.Region);
        region = ReadString(configuration, "AWS_REGION", region);
        options.Region = region;

        options.ModelIds = ReadCsv(configuration, "BEDROCK_MODEL_IDS", options.ModelIds);
        options.EnableCostQueries = ReadBool(configuration, "BEDROCK_ENABLE_COST_QUERIES", options.EnableCostQueries);
    }

    private static string ReadString(IConfiguration configuration, string key, string current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static string? ReadOptionalString(IConfiguration configuration, string key, string? current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value) ? current : value.Trim();
    }

    private static string[] ReadCsv(IConfiguration configuration, string key, string[] current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? current.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool ReadBool(IConfiguration configuration, string key, bool current)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return current;
        }
        return bool.TryParse(value, out bool parsed) ? parsed : current;
    }
}
