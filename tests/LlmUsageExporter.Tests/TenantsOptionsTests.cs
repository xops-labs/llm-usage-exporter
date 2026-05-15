// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Configuration;
using Microsoft.Extensions.Configuration;

namespace LlmUsageExporter.Tests;

public sealed class TenantsOptionsTests
{
    [Fact]
    public void Bind_ParsesItemsAndApiKeysFromConfiguration()
    {
        Dictionary<string, string?> values = new()
        {
            ["Tenants:Items:0:Id"] = "alpha",
            ["Tenants:Items:0:Name"] = "Alpha Corp",
            ["Tenants:Items:0:OpenAi:BaseUrl"] = "https://api.openai.com",
            ["Tenants:Items:0:OpenAi:AdminApiKey"] = "sk-alpha",
            ["Tenants:Items:1:Id"] = "beta",
            ["Tenants:Items:1:Name"] = "Beta Inc",
            ["Tenants:Items:1:Anthropic:BaseUrl"] = "https://api.anthropic.com",
            ["Tenants:Items:1:Anthropic:AdminApiKey"] = "sk-beta",
            ["Tenants:ApiKeys:alpha"] = "token-alpha",
            ["Tenants:ApiKeys:beta"] = "token-beta",
        };

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        TenantsOptions options = new();
        configuration.GetSection(TenantsOptions.SectionName).Bind(options);

        Assert.Equal(2, options.Items.Length);

        TenantConfig alpha = options.Items[0];
        Assert.Equal("alpha", alpha.Id);
        Assert.Equal("Alpha Corp", alpha.Name);
        Assert.NotNull(alpha.OpenAi);
        Assert.Equal("sk-alpha", alpha.OpenAi!.AdminApiKey);
        Assert.Null(alpha.Anthropic);

        TenantConfig beta = options.Items[1];
        Assert.Equal("beta", beta.Id);
        Assert.NotNull(beta.Anthropic);
        Assert.Equal("sk-beta", beta.Anthropic!.AdminApiKey);
        Assert.Null(beta.OpenAi);

        Assert.Equal(2, options.ApiKeys.Count);
        Assert.Equal("token-alpha", options.ApiKeys["alpha"]);
        Assert.Equal("token-beta", options.ApiKeys["beta"]);
    }

    [Fact]
    public void Bind_DefaultsToEmptyItemsAndApiKeys()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        TenantsOptions options = new();
        configuration.GetSection(TenantsOptions.SectionName).Bind(options);

        Assert.Empty(options.Items);
        Assert.Empty(options.ApiKeys);
    }
}
