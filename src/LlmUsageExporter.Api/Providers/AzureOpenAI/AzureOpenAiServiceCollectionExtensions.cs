// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Tracing;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.AzureOpenAI;

public static class AzureOpenAiServiceCollectionExtensions
{
    private const string ProviderName = "azure_openai";
    private const string HttpClientName = "azure-openai";

    public static IServiceCollection AddAzureOpenAiProvider(this IServiceCollection services, IConfiguration configuration)
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
        // has not configured Azure OpenAI. Without this guard the ValidateOnStart()
        // chain below crashes deployments that only want one of the other providers.
        if (!IsProviderConfigured(configuration))
        {
            return services;
        }

        services.Configure<AzureOpenAiOptions>(configuration.GetSection(AzureOpenAiOptions.SectionName));
        services.PostConfigure<AzureOpenAiOptions>(options => ApplyEnvironment(options, configuration));
        services
            .AddOptions<AzureOpenAiOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.TenantId), "AZURE_OPENAI_TENANT_ID is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClientId), "AZURE_OPENAI_CLIENT_ID is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.ClientSecret), "AZURE_OPENAI_CLIENT_SECRET is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.SubscriptionId), "AZURE_OPENAI_SUBSCRIPTION_ID is required.")
            .Validate(options => options.AccountResourceIds.Length > 0, "AZURE_OPENAI_ACCOUNT_RESOURCE_IDS must include at least one resource ID.")
            .Validate(options => Uri.TryCreate(options.ManagementBaseUrl, UriKind.Absolute, out _), "AzureOpenAI:ManagementBaseUrl must be an absolute URI.")
            .Validate(options => Uri.TryCreate(options.LoginBaseUrl, UriKind.Absolute, out _), "AzureOpenAI:LoginBaseUrl must be an absolute URI.")
            .Validate(options => options.MaxRetries >= 0, "AzureOpenAI:MaxRetries must be zero or greater.")
            .ValidateOnStart();
        services.AddSingleton<AzureOpenAiUsageProvider>();
        services.AddHttpClient<AzureOpenAiUsageClient>(httpClient =>
        {
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        }).AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        services.AddSingleton<LlmProviderRegistration>(sp =>
        {
            LlmMetricsPublisher prometheus = new("azure_openai", Prometheus.Metrics.DefaultFactory, sp.GetRequiredService<ICheckpointStore>(), TenantContext.Default);
            LlmMeterPublisher? otel = sp.GetService<LlmMeterPublisher>();
            ILlmMetricsPublisher publisher = otel is null
                ? prometheus
                : new CompositeMetricsPublisher(
                    new ILlmMetricsPublisher[] { prometheus, otel },
                    sp.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

            return new LlmProviderRegistration(
                ProviderName,
                sp.GetRequiredService<AzureOpenAiUsageProvider>(),
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
            if (tenantConfig.AzureOpenAi is null || string.IsNullOrWhiteSpace(tenantConfig.Id))
            {
                continue;
            }

            TenantConfig captured = tenantConfig;
            AzureOpenAiOptions providerOptions = captured.AzureOpenAi!;
            TenantContext tenant = new(captured.Id, string.IsNullOrWhiteSpace(captured.Name) ? captured.Id : captured.Name);

            services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
            {
                IHttpClientFactory factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                HttpClient httpClient = factory.CreateClient(HttpClientName);

                IOptions<AzureOpenAiOptions> wrappedOptions = Options.Create(providerOptions);
                ILogger<AzureOpenAiUsageClient> clientLogger = serviceProvider.GetRequiredService<ILogger<AzureOpenAiUsageClient>>();
                AzureOpenAiUsageClient client = new(httpClient, wrappedOptions, clientLogger);
                AzureOpenAiUsageProvider provider = new(client, providerOptions, tenant);

                ICheckpointStore checkpointStore = serviceProvider.GetRequiredService<ICheckpointStore>();
                LlmMetricsPublisher prometheus = new("azure_openai", Prometheus.Metrics.DefaultFactory, checkpointStore, tenant);
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
    /// Detects whether the operator has opted into the Azure OpenAI provider in single-tenant mode.
    /// <c>AZURE_OPENAI_TENANT_ID</c> is the sentinel — it's mandatory for Azure AD
    /// client-credentials and is the most distinctive value to gate on.
    /// </summary>
    private static bool IsProviderConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["AZURE_OPENAI_TENANT_ID"])
            || !string.IsNullOrWhiteSpace(configuration[$"{AzureOpenAiOptions.SectionName}:TenantId"]);
    }

    private static void ApplyEnvironment(AzureOpenAiOptions options, IConfiguration configuration)
    {
        options.TenantId = ReadString(configuration, "AZURE_OPENAI_TENANT_ID", options.TenantId);
        options.ClientId = ReadString(configuration, "AZURE_OPENAI_CLIENT_ID", options.ClientId);
        options.ClientSecret = ReadString(configuration, "AZURE_OPENAI_CLIENT_SECRET", options.ClientSecret);
        options.SubscriptionId = ReadString(configuration, "AZURE_OPENAI_SUBSCRIPTION_ID", options.SubscriptionId);
        options.ResourceGroup = ReadOptionalString(configuration, "AZURE_OPENAI_RESOURCE_GROUP", options.ResourceGroup);
        options.AccountResourceIds = ReadCsv(configuration, "AZURE_OPENAI_ACCOUNT_RESOURCE_IDS", options.AccountResourceIds);
        options.Models = ReadCsv(configuration, "AZURE_OPENAI_MODELS", options.Models);
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
}
