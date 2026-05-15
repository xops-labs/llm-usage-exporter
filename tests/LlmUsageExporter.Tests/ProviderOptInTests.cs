// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Providers.Anthropic;
using LlmUsageExporter.Api.Providers.AzureOpenAI;
using LlmUsageExporter.Api.Providers.Bedrock;
using LlmUsageExporter.Api.Providers.Gemini;
using LlmUsageExporter.Api.Providers.OpenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmUsageExporter.Tests;

/// <summary>
/// Verifies that in single-tenant mode each provider's DI extension skips registration
/// entirely when the operator has not configured that provider. This is what makes the
/// README's "every provider is independently opt-in" promise true at runtime — without
/// the guard, ValidateOnStart() would crash any deployment that wanted only some of the
/// five providers.
/// </summary>
public sealed class ProviderOptInTests
{
    private static IServiceProvider BuildProviderWith(Action<IServiceCollection, IConfiguration> register, IDictionary<string, string?> config)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ICheckpointStore>(new InMemoryCheckpointStore());
        register(services, configuration);
        return services.BuildServiceProvider();
    }

    private static LlmProviderRegistration[] Registrations(IServiceProvider provider, string providerName) =>
        provider.GetServices<LlmProviderRegistration>().Where(r => r.Name == providerName).ToArray();

    // ───── OpenAI ─────────────────────────────────────────────────────────

    [Fact]
    public void OpenAi_NotConfigured_RegistersNothing()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddOpenAiProvider(c),
            new Dictionary<string, string?>());

        Assert.Empty(Registrations(sp, "openai"));
    }

    [Fact]
    public void OpenAi_ConfiguredViaEnvVar_RegistersSingleTenant()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddOpenAiProvider(c),
            new Dictionary<string, string?> { ["OPENAI_ADMIN_API_KEY"] = "sk-from-env" });

        Assert.Single(Registrations(sp, "openai"));
    }

    [Fact]
    public void OpenAi_ConfiguredViaSection_RegistersSingleTenant()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddOpenAiProvider(c),
            new Dictionary<string, string?> { ["OpenAI:AdminApiKey"] = "sk-from-section" });

        Assert.Single(Registrations(sp, "openai"));
    }

    // ───── Azure OpenAI ───────────────────────────────────────────────────

    [Fact]
    public void AzureOpenAi_NotConfigured_RegistersNothing()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddAzureOpenAiProvider(c),
            new Dictionary<string, string?>());

        Assert.Empty(Registrations(sp, "azure_openai"));
    }

    [Fact]
    public void AzureOpenAi_ConfiguredViaEnvVar_RegistersSingleTenant()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddAzureOpenAiProvider(c),
            new Dictionary<string, string?>
            {
                ["AZURE_OPENAI_TENANT_ID"] = "00000000-0000-0000-0000-000000000000",
                ["AZURE_OPENAI_CLIENT_ID"] = "00000000-0000-0000-0000-000000000000",
                ["AZURE_OPENAI_CLIENT_SECRET"] = "secret",
                ["AZURE_OPENAI_SUBSCRIPTION_ID"] = "00000000-0000-0000-0000-000000000000",
                ["AZURE_OPENAI_ACCOUNT_RESOURCE_IDS"] = "/subscriptions/x/resourceGroups/y/providers/Microsoft.CognitiveServices/accounts/z",
            });

        Assert.Single(Registrations(sp, "azure_openai"));
    }

    // ───── Anthropic ──────────────────────────────────────────────────────

    [Fact]
    public void Anthropic_NotConfigured_RegistersNothing()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddAnthropicProvider(c),
            new Dictionary<string, string?>());

        Assert.Empty(Registrations(sp, "anthropic"));
    }

    [Fact]
    public void Anthropic_ConfiguredViaEnvVar_RegistersSingleTenant()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddAnthropicProvider(c),
            new Dictionary<string, string?> { ["ANTHROPIC_ADMIN_API_KEY"] = "sk-ant-admin" });

        Assert.Single(Registrations(sp, "anthropic"));
    }

    // ───── Gemini ─────────────────────────────────────────────────────────

    [Fact]
    public void Gemini_NotConfigured_RegistersNothing()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddGeminiProvider(c),
            new Dictionary<string, string?>());

        Assert.Empty(Registrations(sp, "gemini"));
    }

    [Fact]
    public void Gemini_ConfiguredViaEnvVar_RegistersSingleTenant()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddGeminiProvider(c),
            new Dictionary<string, string?>
            {
                ["GEMINI_PROJECT_ID"] = "my-gcp-project",
                ["GEMINI_ACCESS_TOKEN"] = "ya29.access-token",
            });

        Assert.Single(Registrations(sp, "gemini"));
    }

    // ───── Bedrock ────────────────────────────────────────────────────────

    [Fact]
    public void Bedrock_NotConfigured_RegistersNothing()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddBedrockProvider(c),
            new Dictionary<string, string?>());

        Assert.Empty(Registrations(sp, "bedrock"));
    }

    [Fact]
    public void Bedrock_ConfiguredViaEnvVar_RegistersSingleTenant()
    {
        using ServiceProvider sp = (ServiceProvider)BuildProviderWith(
            (s, c) => s.AddBedrockProvider(c),
            new Dictionary<string, string?>
            {
                ["AWS_ACCESS_KEY_ID"] = "AKIAEXAMPLE",
                ["AWS_SECRET_ACCESS_KEY"] = "secret",
            });

        Assert.Single(Registrations(sp, "bedrock"));
    }

    // ───── Cross-provider integration ─────────────────────────────────────

    [Fact]
    public void AllFiveProviders_OnlyOpenAiConfigured_OnlyOpenAiRegisters()
    {
        // This is the README's "every provider is independently opt-in" promise as a test.
        // Before the opt-in fix, calling all five AddXProvider() with only OPENAI_ADMIN_API_KEY
        // set would still register the other four and ValidateOnStart() would crash on first poll.
        Dictionary<string, string?> values = new() { ["OPENAI_ADMIN_API_KEY"] = "sk-only-openai" };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton<ICheckpointStore>(new InMemoryCheckpointStore());
        services.AddOpenAiProvider(configuration);
        services.AddAzureOpenAiProvider(configuration);
        services.AddAnthropicProvider(configuration);
        services.AddGeminiProvider(configuration);
        services.AddBedrockProvider(configuration);

        using ServiceProvider sp = services.BuildServiceProvider();
        LlmProviderRegistration[] registrations = sp.GetServices<LlmProviderRegistration>().ToArray();

        Assert.Single(registrations);
        Assert.Equal("openai", registrations[0].Name);
    }
}
