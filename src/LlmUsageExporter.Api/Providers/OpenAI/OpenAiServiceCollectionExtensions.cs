// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Tracing;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.OpenAI;

public static class OpenAiServiceCollectionExtensions
{
    public const string ProviderName = "openai";

    public static IServiceCollection AddOpenAiProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind the multi-tenant root once; idempotent on repeated calls.
        services.Configure<TenantsOptions>(configuration.GetSection(TenantsOptions.SectionName));

        TenantsOptions tenantsSnapshot = new();
        configuration.GetSection(TenantsOptions.SectionName).Bind(tenantsSnapshot);

        if (tenantsSnapshot.Items.Length > 0)
        {
            RegisterMultiTenant(services, tenantsSnapshot);
            return services;
        }

        // Single-tenant opt-in check: only register OpenAI if the user has set the
        // admin key (env var or appsettings section). Without this guard, the
        // .ValidateOnStart() below crashes any deployment that wants other providers
        // but not OpenAI — contradicting the README's "providers are opt-in" promise.
        if (!IsProviderConfigured(configuration))
        {
            return services;
        }

        // Single-tenant (backward compat) registration path — unchanged behavior.
        services.Configure<OpenAiOptions>(configuration.GetSection(OpenAiOptions.SectionName));
        services.PostConfigure<OpenAiOptions>(options => ApplyEnvironment(options, configuration));

        services
            .AddOptions<OpenAiOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.AdminApiKey), "OPENAI_ADMIN_API_KEY is required.")
            .Validate(options => Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _), "OpenAI:BaseUrl must be an absolute URI.")
            .Validate(options => options.MaxRetries >= 0, "OpenAI:MaxRetries must be zero or greater.")
            .ValidateOnStart();
        services.AddSingleton<OpenAiUsageProvider>();
        services.AddHttpClient<OpenAiUsageClient>((serviceProvider, httpClient) =>
        {
            OpenAiOptions options = serviceProvider.GetRequiredService<IOptions<OpenAiOptions>>().Value;
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        }).AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
        {
            LlmMetricsPublisher prometheus = new("openai", Prometheus.Metrics.DefaultFactory, serviceProvider.GetRequiredService<ICheckpointStore>(), TenantContext.Default);
            LlmMeterPublisher? otel = serviceProvider.GetService<LlmMeterPublisher>();
            ILlmMetricsPublisher publisher = otel is null
                ? prometheus
                : new CompositeMetricsPublisher(
                    new ILlmMetricsPublisher[] { prometheus, otel },
                    serviceProvider.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

            return new LlmProviderRegistration(
                ProviderName,
                serviceProvider.GetRequiredService<OpenAiUsageProvider>(),
                publisher);
        });

        return services;
    }

    private static void RegisterMultiTenant(IServiceCollection services, TenantsOptions tenantsSnapshot)
    {
        services.AddHttpClient(ProviderName, client => client.Timeout = TimeSpan.FromSeconds(60))
            .AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        foreach (TenantConfig tenantConfig in tenantsSnapshot.Items)
        {
            if (tenantConfig.OpenAi is null || string.IsNullOrWhiteSpace(tenantConfig.Id))
            {
                continue;
            }

            TenantConfig captured = tenantConfig;
            OpenAiOptions providerOptions = captured.OpenAi!;
            TenantContext tenant = new(captured.Id, string.IsNullOrWhiteSpace(captured.Name) ? captured.Id : captured.Name);

            services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
            {
                IHttpClientFactory factory = serviceProvider.GetRequiredService<IHttpClientFactory>();
                HttpClient httpClient = factory.CreateClient(ProviderName);
                if (httpClient.BaseAddress is null && Uri.TryCreate(providerOptions.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? baseUri))
                {
                    httpClient.BaseAddress = baseUri;
                }

                IOptions<OpenAiOptions> wrappedOptions = Options.Create(providerOptions);
                ILogger<OpenAiUsageClient> clientLogger = serviceProvider.GetRequiredService<ILogger<OpenAiUsageClient>>();
                OpenAiUsageClient client = new(httpClient, wrappedOptions, clientLogger);
                OpenAiUsageProvider provider = new(client, providerOptions, tenant);

                ICheckpointStore checkpointStore = serviceProvider.GetRequiredService<ICheckpointStore>();
                LlmMetricsPublisher prometheus = new("openai", Prometheus.Metrics.DefaultFactory, checkpointStore, tenant);
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
    /// Detects whether the operator has opted into the OpenAI provider in single-tenant mode.
    /// Returns true when either the <c>OPENAI_ADMIN_API_KEY</c> env var or the
    /// <c>OpenAI:AdminApiKey</c> appsettings value is set.
    /// </summary>
    private static bool IsProviderConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["OPENAI_ADMIN_API_KEY"])
            || !string.IsNullOrWhiteSpace(configuration[$"{OpenAiOptions.SectionName}:AdminApiKey"]);
    }

    private static void ApplyEnvironment(OpenAiOptions options, IConfiguration configuration)
    {
        options.AdminApiKey = ReadString(configuration, "OPENAI_ADMIN_API_KEY", options.AdminApiKey);
        options.OrganizationId = ReadOptionalString(configuration, "OPENAI_ORG_ID", options.OrganizationId);
        options.ProjectIds = ReadCsv(configuration, "OPENAI_PROJECT_IDS", options.ProjectIds);
        options.Models = ReadCsv(configuration, "OPENAI_MODELS", options.Models);
        options.ApiKeyIds = ReadCsv(configuration, "OPENAI_API_KEY_IDS", options.ApiKeyIds);
        options.UserIds = ReadCsv(configuration, "OPENAI_USER_IDS", options.UserIds);
        options.GroupBy = ReadCsv(configuration, "EXPORTER_GROUP_BY", options.GroupBy);
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
