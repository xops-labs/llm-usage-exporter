// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Providers.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LlmUsageExporter.Tests;

public sealed class MultiTenantRegistrationTests
{
    [Fact]
    public void AddOpenAiProvider_WithTwoTenants_RegistersOneRegistrationPerTenant()
    {
        Dictionary<string, string?> values = new()
        {
            ["Tenants:Items:0:Id"] = "alpha",
            ["Tenants:Items:0:Name"] = "Alpha Corp",
            ["Tenants:Items:0:OpenAi:BaseUrl"] = "https://api.openai.com",
            ["Tenants:Items:0:OpenAi:AdminApiKey"] = "sk-alpha",
            ["Tenants:Items:1:Id"] = "beta",
            ["Tenants:Items:1:Name"] = "Beta Inc",
            ["Tenants:Items:1:OpenAi:BaseUrl"] = "https://api.openai.com",
            ["Tenants:Items:1:OpenAi:AdminApiKey"] = "sk-beta",
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ICheckpointStore>(new InMemoryCheckpointStore());
        services.AddOpenAiProvider(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        LlmProviderRegistration[] registrations = provider
            .GetServices<LlmProviderRegistration>()
            .ToArray();

        Assert.Equal(2, registrations.Length);
        Assert.Contains(registrations, r => r.Tenant.Id == "alpha" && r.Name == "openai");
        Assert.Contains(registrations, r => r.Tenant.Id == "beta" && r.Name == "openai");
    }

    [Fact]
    public void AddOpenAiProvider_WithoutTenants_KeepsSingleTenantRegistration()
    {
        // Single-tenant default — backward compat path. Use a populated OpenAI config so validation passes.
        Dictionary<string, string?> values = new()
        {
            ["OpenAI:BaseUrl"] = "https://api.openai.com",
            ["OpenAI:AdminApiKey"] = "sk-classic",
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ICheckpointStore>(new InMemoryCheckpointStore());
        services.AddOpenAiProvider(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        LlmProviderRegistration[] registrations = provider
            .GetServices<LlmProviderRegistration>()
            .ToArray();

        LlmProviderRegistration registration = Assert.Single(registrations);
        Assert.Equal("openai", registration.Name);
        Assert.Equal("default", registration.Tenant.Id);
    }
}
