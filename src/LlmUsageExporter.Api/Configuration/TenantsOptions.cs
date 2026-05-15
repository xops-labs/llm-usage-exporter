// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Configuration;

public sealed class TenantsOptions
{
    public const string SectionName = "Tenants";

    // Empty list = single-tenant mode (backward compat). Non-empty = multi-tenant.
    public TenantConfig[] Items { get; set; } = [];

    // Optional per-tenant bearer tokens for /metrics?tenant=<id> auth.
    public Dictionary<string, string> ApiKeys { get; set; } = new();
}

public sealed class TenantConfig
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public OpenAiOptions? OpenAi { get; set; }

    public AzureOpenAiOptions? AzureOpenAi { get; set; }

    public AnthropicOptions? Anthropic { get; set; }

    public GeminiOptions? Gemini { get; set; }

    public BedrockOptions? Bedrock { get; set; }
}
