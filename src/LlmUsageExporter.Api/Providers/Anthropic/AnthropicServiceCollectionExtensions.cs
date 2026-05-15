// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Tracing;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Anthropic;

public static class AnthropicServiceCollectionExtensions
{
    private const string ProviderName = "anthropic";
    private const string HttpClientName = "anthropic";

    public static IServiceCollection AddAnthropicProvider(this IServiceCollection services, IConfiguration configuration)
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
        // has not configured Anthropic. Without this guard the ValidateOnStart()
        // chain below crashes deployments that only want one of the other providers.
        if (!IsProviderConfigured(configuration))
        {
            return services;
        }

        services.Configure<AnthropicOptions>(configuration.GetSection(AnthropicOptions.SectionName));
        services.PostConfigure<AnthropicOptions>(options => ApplyEnvironment(options, configuration));
        services
            .AddOptions<AnthropicOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.AdminApiKey), "ANTHROPIC_ADMIN_API_KEY is required.")
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "Anthropic:BaseUrl must be an absolute URI.")
            .Validate(options => options.MaxRetries >= 0, "Anthropic:MaxRetries must be zero or greater.")
            .ValidateOnStart();
        services.AddSingleton<AnthropicUsageProvider>();
        services.AddHttpClient<AnthropicUsageClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<AnthropicOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        }).AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
        {
            LlmMetricsPublisher prometheus = new("anthropic", Prometheus.Metrics.DefaultFactory, serviceProvider.GetRequiredService<ICheckpointStore>(), TenantContext.Default);
            LlmMeterPublisher? otel = serviceProvider.GetService<LlmMeterPublisher>();
            ILlmMetricsPublisher publisher = otel is null
                ? prometheus
                : new CompositeMetricsPublisher(
                    new ILlmMetricsPublisher[] { prometheus, otel },
                    serviceProvider.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

            return new LlmProviderRegistration(
                ProviderName,
                serviceProvider.GetRequiredService<AnthropicUsageProvider>(),
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
            if (tenantConfig.Anthropic is null || string.IsNullOrWhiteSpace(tenantConfig.Id))
            {
                continue;
            }

            TenantConfig captured = tenantConfig;
            AnthropicOptions providerOptions = captured.Anthropic!;
            TenantContext tenant = new(captured.Id, string.IsNullOrWhiteSpace(captured.Name) ? captured.Id : captured.Name);

            services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
            {
                IHttpClientFactory factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                HttpClient httpClient = factory.CreateClient(HttpClientName);
                if (httpClient.BaseAddress is null && Uri.TryCreate(providerOptions.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri))
                {
                    httpClient.BaseAddress = baseUri;
                }

                IOptions<AnthropicOptions> wrappedOptions = Options.Create(providerOptions);
                ILogger<AnthropicUsageClient> clientLogger = serviceProvider.GetRequiredService<ILogger<AnthropicUsageClient>>();
                AnthropicUsageClient client = new(httpClient, wrappedOptions, clientLogger);
                AnthropicUsageProvider provider = new(client, providerOptions, tenant);

                ICheckpointStore checkpointStore = serviceProvider.GetRequiredService<ICheckpointStore>();
                LlmMetricsPublisher prometheus = new("anthropic", Prometheus.Metrics.DefaultFactory, checkpointStore, tenant);
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
    /// Detects whether the operator has opted into the Anthropic provider in single-tenant mode.
    /// The admin API key serves as the sentinel.
    /// </summary>
    private static bool IsProviderConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["ANTHROPIC_ADMIN_API_KEY"])
            || !string.IsNullOrWhiteSpace(configuration[$"{AnthropicOptions.SectionName}:AdminApiKey"]);
    }

    private static void ApplyEnvironment(AnthropicOptions options, IConfiguration configuration)
    {
        options.AdminApiKey = ReadString(configuration, "ANTHROPIC_ADMIN_API_KEY", options.AdminApiKey);
        options.AnthropicVersion = ReadString(configuration, "ANTHROPIC_VERSION", options.AnthropicVersion);
        options.WorkspaceIds = ReadCsv(configuration, "ANTHROPIC_WORKSPACE_IDS", options.WorkspaceIds);
        options.Models = ReadCsv(configuration, "ANTHROPIC_MODELS", options.Models);
        options.ApiKeyIds = ReadCsv(configuration, "ANTHROPIC_API_KEY_IDS", options.ApiKeyIds);
    }

    private static string ReadString(IConfiguration configuration, string key, string current)
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
