// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Providers.Abstractions;
using Prometheus;

namespace LlmUsageExporter.Tests;

public sealed class PublisherTenantLabelTests
{
    [Fact]
    public async Task PublishUsage_EmitsTenantLabelFromInjectedContext()
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        TenantContext tenant = new("my-tenant-id", "My Tenant");
        var publisher = new LlmMetricsPublisher(
            "openai",
            Prometheus.Metrics.WithCustomRegistry(registry),
            new InMemoryCheckpointStore(),
            tenant);

        // Bucket whose Tenant matches the injected context; the publisher should
        // label-encode it as tenant="my-tenant-id".
        var bucket = new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "openai",
            "gpt-test",
            "proj_123",
            10,
            5,
            0,
            15,
            1)
        {
            Tenant = "my-tenant-id"
        };

        publisher.PublishUsage([bucket]);

        string metrics = await ScrapeAsync(registry);

        Assert.Contains("""llm_usage_input_tokens_total{tenant="my-tenant-id",provider="openai",model="gpt-test",tenancy_id="proj_123"} 10""", metrics);
    }

    [Fact]
    public async Task PublishUsage_DefaultPublisherEmitsDefaultTenantLabel()
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        var publisher = new LlmMetricsPublisher("openai", Prometheus.Metrics.WithCustomRegistry(registry));

        var bucket = new LlmUsageBucket(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "openai",
            "gpt-test",
            "proj_123",
            10,
            5,
            0,
            15,
            1);

        publisher.PublishUsage([bucket]);

        string metrics = await ScrapeAsync(registry);

        Assert.Contains("""llm_usage_input_tokens_total{tenant="default",provider="openai",model="gpt-test",tenancy_id="proj_123"} 10""", metrics);
    }

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
