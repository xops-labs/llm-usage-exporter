// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Providers.Abstractions;

/// <summary>
/// Provider-neutral usage bucket. <see cref="TenancyId"/> is the per-provider
/// tenancy slot — it holds whichever native identifier the upstream API uses to
/// scope organization-level usage: OpenAI / Gemini project_id, Anthropic
/// workspace_id, Azure OpenAI resource_id, AWS Bedrock region. The provider name
/// disambiguates which kind of ID this is.
/// </summary>
public sealed record LlmUsageBucket(
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    string Provider,
    string? Model,
    string? TenancyId,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long TotalTokens,
    long RequestCount)
{
    public string Tenant { get; init; } = "default";
}
