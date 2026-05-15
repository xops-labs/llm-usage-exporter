// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Tracing;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Gemini;

public static class GeminiServiceCollectionExtensions
{
    private const string ProviderName = "gemini";
    private const string TokenHttpClientName = "gemini-token";
    private const string UsageHttpClientName = "gemini-usage";

    public static IServiceCollection AddGeminiProvider(this IServiceCollection services, IConfiguration configuration)
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
        // has not configured Gemini. Without this guard the ValidateOnStart()
        // chain below crashes deployments that only want one of the other providers.
        if (!IsProviderConfigured(configuration))
        {
            return services;
        }

        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));
        services.PostConfigure<GeminiOptions>(options => ApplyGeminiEnvironment(options, configuration));
        services
            .AddOptions<GeminiOptions>()
            .Validate(options => !string.IsNullOrWhiteSpace(options.ProjectId), "Gemini:ProjectId is required.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.AccessToken) || !string.IsNullOrWhiteSpace(options.ServiceAccountKeyFile),
                "Gemini provider requires Gemini:AccessToken or Gemini:ServiceAccountKeyFile to be configured.")
            .Validate(options => Uri.TryCreate(options.MonitoringBaseUrl, UriKind.Absolute, out _), "Gemini:MonitoringBaseUrl must be an absolute URI.")
            .Validate(options => Uri.TryCreate(options.BigQueryBaseUrl, UriKind.Absolute, out _), "Gemini:BigQueryBaseUrl must be an absolute URI.")
            .Validate(options => Uri.TryCreate(options.OAuthBaseUrl, UriKind.Absolute, out _), "Gemini:OAuthBaseUrl must be an absolute URI.")
            .Validate(options => options.MaxRetries >= 0, "Gemini:MaxRetries must be zero or greater.")
            .ValidateOnStart();
        services.AddSingleton<GeminiUsageProvider>();

        services.AddHttpClient<GeminiTokenProvider>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptionsMonitor<GeminiOptions>>().CurrentValue;
            httpClient.BaseAddress = new Uri(options.OAuthBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            httpClient.Timeout = TimeSpan.FromSeconds(30);
        }).AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        services.AddHttpClient<GeminiUsageClient>((serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptionsMonitor<GeminiOptions>>().CurrentValue;
            httpClient.BaseAddress = new Uri(options.MonitoringBaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        }).AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
        {
            LlmMetricsPublisher prometheus = new("gemini", Prometheus.Metrics.DefaultFactory, serviceProvider.GetRequiredService<ICheckpointStore>(), TenantContext.Default);
            LlmMeterPublisher? otel = serviceProvider.GetService<LlmMeterPublisher>();
            ILlmMetricsPublisher publisher = otel is null
                ? prometheus
                : new CompositeMetricsPublisher(
                    new ILlmMetricsPublisher[] { prometheus, otel },
                    serviceProvider.GetRequiredService<ILogger<CompositeMetricsPublisher>>());

            return new LlmProviderRegistration(
                ProviderName,
                serviceProvider.GetRequiredService<GeminiUsageProvider>(),
                publisher);
        });

        return services;
    }

    private static void RegisterMultiTenant(IServiceCollection services, TenantsOptions tenantsSnapshot)
    {
        services.AddHttpClient(TokenHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));
        services.AddHttpClient(UsageHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(60))
            .AddHttpMessageHandler(sp => new TraceContextSuppressionHandler(sp.GetService<OtlpOptions>() ?? new OtlpOptions()));

        foreach (TenantConfig tenantConfig in tenantsSnapshot.Items)
        {
            if (tenantConfig.Gemini is null || string.IsNullOrWhiteSpace(tenantConfig.Id))
            {
                continue;
            }

            TenantConfig captured = tenantConfig;
            GeminiOptions providerOptions = captured.Gemini!;
            TenantContext tenant = new(captured.Id, string.IsNullOrWhiteSpace(captured.Name) ? captured.Id : captured.Name);
            IOptionsMonitor<GeminiOptions> staticMonitor = new StaticOptionsMonitor<GeminiOptions>(providerOptions);

            services.AddSingleton<LlmProviderRegistration>(serviceProvider =>
            {
                IHttpClientFactory factory = serviceProvider.GetRequiredService<IHttpClientFactory>();

                HttpClient tokenHttpClient = factory.CreateClient(TokenHttpClientName);
                if (tokenHttpClient.BaseAddress is null && Uri.TryCreate(providerOptions.OAuthBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? oauthBase))
                {
                    tokenHttpClient.BaseAddress = oauthBase;
                }

                HttpClient usageHttpClient = factory.CreateClient(UsageHttpClientName);
                if (usageHttpClient.BaseAddress is null && Uri.TryCreate(providerOptions.MonitoringBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out Uri? monitoringBase))
                {
                    usageHttpClient.BaseAddress = monitoringBase;
                }

                ILogger<GeminiTokenProvider> tokenLogger = serviceProvider.GetRequiredService<ILogger<GeminiTokenProvider>>();
                GeminiTokenProvider tokenProvider = new(tokenHttpClient, staticMonitor, tokenLogger);

                ILogger<GeminiUsageClient> usageLogger = serviceProvider.GetRequiredService<ILogger<GeminiUsageClient>>();
                GeminiUsageClient client = new(usageHttpClient, staticMonitor, tokenProvider, usageLogger);

                GeminiUsageProvider provider = new(client, staticMonitor, tenant);

                ICheckpointStore checkpointStore = serviceProvider.GetRequiredService<ICheckpointStore>();
                LlmMetricsPublisher prometheus = new("gemini", Prometheus.Metrics.DefaultFactory, checkpointStore, tenant);
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
    /// Detects whether the operator has opted into the Gemini provider in single-tenant mode.
    /// <c>GEMINI_PROJECT_ID</c> is the sentinel — required for every Cloud Monitoring call.
    /// </summary>
    private static bool IsProviderConfigured(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["GEMINI_PROJECT_ID"])
            || !string.IsNullOrWhiteSpace(configuration[$"{GeminiOptions.SectionName}:ProjectId"]);
    }

    private static void ApplyGeminiEnvironment(GeminiOptions options, IConfiguration configuration)
    {
        options.ProjectId = ReadString(configuration, "GEMINI_PROJECT_ID", options.ProjectId);
        options.BillingProjectId = ReadOptionalString(configuration, "GEMINI_BILLING_PROJECT_ID", options.BillingProjectId);
        options.BillingDatasetProject = ReadOptionalString(configuration, "GEMINI_BILLING_DATASET_PROJECT", options.BillingDatasetProject);
        options.BillingDatasetId = ReadOptionalString(configuration, "GEMINI_BILLING_DATASET_ID", options.BillingDatasetId);
        options.BillingTable = ReadOptionalString(configuration, "GEMINI_BILLING_TABLE", options.BillingTable);
        options.EnableCostQueries = ReadBool(configuration, "GEMINI_ENABLE_COST_QUERIES", options.EnableCostQueries);
        options.AccessToken = ReadOptionalString(configuration, "GEMINI_ACCESS_TOKEN", options.AccessToken);
        options.ServiceAccountKeyFile = ReadOptionalString(configuration, "GEMINI_SERVICE_ACCOUNT_KEY_FILE", options.ServiceAccountKeyFile);
        options.Models = ReadCsv(configuration, "GEMINI_MODELS", options.Models);
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

    private static bool ReadBool(IConfiguration configuration, string key, bool current)
    {
        string? value = configuration[key];
        return bool.TryParse(value, out bool parsed) ? parsed : current;
    }

    private static string[] ReadCsv(IConfiguration configuration, string key, string[] current)
    {
        string? value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? current.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()).ToArray()
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
