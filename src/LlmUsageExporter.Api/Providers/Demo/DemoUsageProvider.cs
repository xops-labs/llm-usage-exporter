// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Providers.Demo;

/// <summary>
/// Synthetic provider for first-run demos and dashboard previews. Emits
/// deterministic per-slot usage and cost buckets so a developer can see the
/// dashboards alive without configuring real provider credentials.
/// </summary>
/// <remarks>
/// All series carry <c>provider="demo"</c> and use fabricated model names
/// (<c>demo-flagship-large</c> etc.) so they are unambiguously distinguishable
/// from real telemetry. Values are derived from a deterministic hash of
/// (slot, model, tenant) — re-polling the same window produces identical
/// buckets, which keeps the checkpoint-based deduplication in
/// <see cref="LlmUsageExporter.Api.Metrics.LlmMetricsPublisher"/> behaving the
/// same way it does for the real providers.
/// </remarks>
public sealed class DemoUsageProvider : ILlmUsageProvider
{
    public const string ProviderName = "demo";

    private static readonly TimeSpan BucketWidth = TimeSpan.FromMinutes(5);

    private static readonly string[] Models =
    [
        "demo-flagship-large",
        "demo-flagship-mini",
        "demo-fast-haiku",
        "demo-fast-mini",
        "demo-multimodal",
    ];

    private static readonly (string Tenant, string TenancyId)[] Tenants =
    [
        ("demo-team-alpha", "proj_demo_alpha"),
        ("demo-team-beta",  "proj_demo_beta"),
    ];

    public Task<IReadOnlyCollection<LlmUsageBucket>> GetUsageAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        List<LlmUsageBucket> buckets = new();
        foreach (DateTimeOffset slotStart in AlignedSlots(start, end))
        {
            DateTimeOffset slotEnd = slotStart.Add(BucketWidth);
            foreach ((string tenant, string tenancyId) in Tenants)
            {
                foreach (string model in Models)
                {
                    (long input, long output, long cached, long requests, _) = Synthesize(slotStart, model, tenant);
                    buckets.Add(new LlmUsageBucket(
                        slotStart,
                        slotEnd,
                        ProviderName,
                        model,
                        tenancyId,
                        input,
                        output,
                        cached,
                        input + output,
                        requests)
                    {
                        Tenant = tenant
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyCollection<LlmUsageBucket>>(buckets);
    }

    public Task<IReadOnlyCollection<LlmCostBucket>> GetCostsAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        List<LlmCostBucket> buckets = new();
        foreach (DateTimeOffset slotStart in AlignedSlots(start, end))
        {
            DateTimeOffset slotEnd = slotStart.Add(BucketWidth);
            foreach ((string tenant, string tenancyId) in Tenants)
            {
                foreach (string model in Models)
                {
                    (_, _, _, _, decimal cost) = Synthesize(slotStart, model, tenant);
                    buckets.Add(new LlmCostBucket(
                        slotStart,
                        slotEnd,
                        ProviderName,
                        model,
                        tenancyId,
                        cost)
                    {
                        Tenant = tenant
                    });
                }
            }
        }

        return Task.FromResult<IReadOnlyCollection<LlmCostBucket>>(buckets);
    }

    private static IEnumerable<DateTimeOffset> AlignedSlots(DateTimeOffset start, DateTimeOffset end)
    {
        DateTimeOffset slot = AlignDown(start);
        if (slot < start)
        {
            slot = slot.Add(BucketWidth);
        }

        while (slot < end)
        {
            yield return slot;
            slot = slot.Add(BucketWidth);
        }
    }

    private static DateTimeOffset AlignDown(DateTimeOffset timestamp)
    {
        long ticks = timestamp.UtcTicks - (timestamp.UtcTicks % BucketWidth.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static (long Input, long Output, long Cached, long Requests, decimal Cost) Synthesize(
        DateTimeOffset slotStart,
        string model,
        string tenant)
    {
        int seed = HashCode.Combine(slotStart.ToUnixTimeSeconds(), model, tenant);
        Random rng = new(seed);

        // Daily cycle that peaks around 18:00 UTC and troughs around 06:00 UTC,
        // so dashboards always show a recognizable diurnal shape regardless of
        // when a developer first starts the exporter.
        double hourOfDay = slotStart.UtcDateTime.TimeOfDay.TotalHours;
        double daily = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (hourOfDay - 6) / 24);

        double modelWeight = model switch
        {
            "demo-flagship-large" => 1.0,
            "demo-flagship-mini"  => 1.6,
            "demo-fast-haiku"     => 0.9,
            "demo-fast-mini"      => 1.4,
            "demo-multimodal"     => 0.5,
            _                      => 1.0,
        };

        double tenantWeight = tenant == "demo-team-alpha" ? 1.3 : 0.7;

        double baseInput = 80_000 * daily * modelWeight * tenantWeight * (0.85 + (rng.NextDouble() * 0.30));
        long input = (long)baseInput;
        long output = (long)(baseInput * (0.30 + (rng.NextDouble() * 0.20)));
        long cached = (long)(baseInput * (0.10 + (rng.NextDouble() * 0.10)));
        long requests = (long)Math.Max(1, baseInput / 1500.0);

        decimal inputPricePer1K = model switch
        {
            "demo-flagship-large" => 0.0030m,
            "demo-flagship-mini"  => 0.00015m,
            "demo-fast-haiku"     => 0.00025m,
            "demo-fast-mini"      => 0.0001m,
            "demo-multimodal"     => 0.0050m,
            _                      => 0.001m,
        };
        decimal outputPricePer1K = model switch
        {
            "demo-flagship-large" => 0.0100m,
            "demo-flagship-mini"  => 0.00060m,
            "demo-fast-haiku"     => 0.00125m,
            "demo-fast-mini"      => 0.00040m,
            "demo-multimodal"     => 0.0150m,
            _                      => 0.003m,
        };

        decimal cost = ((decimal)input / 1000m * inputPricePer1K)
                     + ((decimal)output / 1000m * outputPricePer1K);

        return (input, output, cached, requests, cost);
    }
}
