// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Providers.Abstractions;
using Prometheus;

namespace LlmUsageExporter.Api.Metrics;

/// <summary>
/// Unified Prometheus publisher shared by every provider. Replaces the five
/// per-provider publishers (Open AI / Azure OpenAI / Anthropic / Gemini /
/// Bedrock). Every series carries a <c>provider</c> label whose value comes
/// from the constructor — the metric families themselves are registered once
/// at the canonical <c>llm_*</c> name regardless of how many providers are
/// active.
/// </summary>
/// <remarks>
/// <para>
/// Metric families registered:
/// <list type="bullet">
///   <item><c>llm_usage_input_tokens_total{tenant, provider, model, tenancy_id}</c></item>
///   <item><c>llm_usage_output_tokens_total{tenant, provider, model, tenancy_id}</c></item>
///   <item><c>llm_usage_total_tokens_total{tenant, provider, model, tenancy_id}</c></item>
///   <item><c>llm_usage_cached_input_tokens_total{tenant, provider, model, tenancy_id}</c></item>
///   <item><c>llm_usage_requests_total{tenant, provider, model, tenancy_id}</c></item>
///   <item><c>llm_usage_cost_usd_total{tenant, provider, tenancy_id}</c></item>
///   <item><c>llm_usage_cost_usd_by_model_total{tenant, provider, model, tenancy_id}</c></item>
///   <item><c>llm_exporter_poll_success_total{tenant, provider}</c></item>
///   <item><c>llm_exporter_poll_failure_total{tenant, provider}</c></item>
///   <item><c>llm_exporter_last_success_timestamp{tenant, provider}</c></item>
///   <item><c>llm_exporter_last_failure_timestamp{tenant, provider}</c></item>
///   <item><c>llm_exporter_last_poll_duration_seconds{tenant, provider}</c></item>
/// </list>
/// </para>
/// <para>
/// The <c>tenancy_id</c> label holds whichever native identifier the provider
/// uses to scope organization-level usage — OpenAI / Gemini project_id,
/// Anthropic workspace_id, Azure OpenAI resource_id, AWS Bedrock region. The
/// provider label disambiguates which kind of ID applies.
/// </para>
/// <para>
/// prometheus-net's <see cref="IMetricFactory.CreateCounter"/> is idempotent on
/// (name, label-names), so constructing multiple publisher instances pointing
/// at the same factory returns the same Counter family — exactly what's needed
/// when each provider's DI extension constructs its own publisher instance.
/// </para>
/// </remarks>
public sealed class LlmMetricsPublisher : ILlmMetricsPublisher
{
    private static readonly string[] UsageLabelNames = ["tenant", "provider", "model", "tenancy_id"];
    private static readonly string[] CostLabelNames = ["tenant", "provider", "tenancy_id"];
    private static readonly string[] ExporterLabelNames = ["tenant", "provider"];

    private readonly string _provider;
    private readonly ICheckpointStore _checkpointStore;
    private readonly TenantContext _tenant;

    private readonly Counter _inputTokens;
    private readonly Counter _outputTokens;
    private readonly Counter _totalTokens;
    private readonly Counter _cachedInputTokens;
    private readonly Counter _requests;
    private readonly Counter _costUsd;
    private readonly Counter _costUsdByModel;
    private readonly Counter _pollSuccess;
    private readonly Counter _pollFailure;
    private readonly Gauge _lastSuccessTimestamp;
    private readonly Gauge _lastFailureTimestamp;
    private readonly Gauge _lastPollDurationSeconds;

    /// <summary>
    /// Default constructor used by tests — wires the global default factory and an
    /// in-memory checkpoint store. Production wiring goes through DI and passes
    /// explicit instances of <see cref="IMetricFactory"/>, <see cref="ICheckpointStore"/>,
    /// and <see cref="TenantContext"/>.
    /// </summary>
    public LlmMetricsPublisher(string provider)
        : this(provider, Prometheus.Metrics.DefaultFactory, new InMemoryCheckpointStore(), TenantContext.Default)
    {
    }

    public LlmMetricsPublisher(string provider, IMetricFactory metricFactory)
        : this(provider, metricFactory, new InMemoryCheckpointStore(), TenantContext.Default)
    {
    }

    public LlmMetricsPublisher(string provider, ICheckpointStore checkpointStore)
        : this(provider, Prometheus.Metrics.DefaultFactory, checkpointStore, TenantContext.Default)
    {
    }

    public LlmMetricsPublisher(string provider, IMetricFactory metricFactory, ICheckpointStore checkpointStore)
        : this(provider, metricFactory, checkpointStore, TenantContext.Default)
    {
    }

    public LlmMetricsPublisher(string provider, IMetricFactory metricFactory, ICheckpointStore checkpointStore, TenantContext tenant)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Provider name is required.", nameof(provider));
        }

        _provider = provider;
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));

        _inputTokens = metricFactory.CreateCounter(
            "llm_usage_input_tokens_total",
            "Aggregated LLM input tokens.",
            new CounterConfiguration { LabelNames = UsageLabelNames });

        _outputTokens = metricFactory.CreateCounter(
            "llm_usage_output_tokens_total",
            "Aggregated LLM output tokens.",
            new CounterConfiguration { LabelNames = UsageLabelNames });

        _totalTokens = metricFactory.CreateCounter(
            "llm_usage_total_tokens_total",
            "Aggregated LLM total tokens.",
            new CounterConfiguration { LabelNames = UsageLabelNames });

        _cachedInputTokens = metricFactory.CreateCounter(
            "llm_usage_cached_input_tokens_total",
            "Aggregated LLM cached input tokens (emitted only when the upstream provider reports prompt-cache hits — currently OpenAI and Anthropic).",
            new CounterConfiguration { LabelNames = UsageLabelNames });

        _requests = metricFactory.CreateCounter(
            "llm_usage_requests_total",
            "Aggregated LLM request count (emitted only by providers whose upstream API reports it — every provider except Anthropic).",
            new CounterConfiguration { LabelNames = UsageLabelNames });

        _costUsd = metricFactory.CreateCounter(
            "llm_usage_cost_usd_total",
            "Operational cost in USD per tenancy, derived from provider billing history APIs. Reflects spend at the time of the poll; subject to per-provider reporting delays (see docs/metrics.md). Not an accounting record.",
            new CounterConfiguration { LabelNames = CostLabelNames });

        _costUsdByModel = metricFactory.CreateCounter(
            "llm_usage_cost_usd_by_model_total",
            "Operational cost in USD per model and tenancy, derived from provider billing history APIs. Subject to the same delay and finalization caveats as llm_usage_cost_usd_total.",
            new CounterConfiguration { LabelNames = UsageLabelNames });

        _pollSuccess = metricFactory.CreateCounter(
            "llm_exporter_poll_success_total",
            "Total successful polling attempts per provider.",
            new CounterConfiguration { LabelNames = ExporterLabelNames });

        _pollFailure = metricFactory.CreateCounter(
            "llm_exporter_poll_failure_total",
            "Total failed polling attempts per provider.",
            new CounterConfiguration { LabelNames = ExporterLabelNames });

        _lastSuccessTimestamp = metricFactory.CreateGauge(
            "llm_exporter_last_success_timestamp",
            "Unix timestamp of the latest successful poll per provider.",
            new GaugeConfiguration { LabelNames = ExporterLabelNames });

        _lastFailureTimestamp = metricFactory.CreateGauge(
            "llm_exporter_last_failure_timestamp",
            "Unix timestamp of the latest failed poll per provider.",
            new GaugeConfiguration { LabelNames = ExporterLabelNames });

        _lastPollDurationSeconds = metricFactory.CreateGauge(
            "llm_exporter_last_poll_duration_seconds",
            "Duration of the latest poll per provider, in seconds.",
            new GaugeConfiguration { LabelNames = ExporterLabelNames });
    }

    public void PublishUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
    {
        foreach (LlmUsageBucket bucket in buckets)
        {
            string tenant = MetricLabelSanitizer.Sanitize(ResolveTenant(bucket.Tenant));
            string model = MetricLabelSanitizer.Sanitize(bucket.Model);
            string tenancyId = MetricLabelSanitizer.Sanitize(bucket.TenancyId);

            PublishCounterOnce(bucket, "input_tokens",        bucket.InputTokens,        () => _inputTokens.WithLabels(tenant, _provider, model, tenancyId).Inc(bucket.InputTokens));
            PublishCounterOnce(bucket, "output_tokens",       bucket.OutputTokens,       () => _outputTokens.WithLabels(tenant, _provider, model, tenancyId).Inc(bucket.OutputTokens));
            PublishCounterOnce(bucket, "total_tokens",        bucket.TotalTokens,        () => _totalTokens.WithLabels(tenant, _provider, model, tenancyId).Inc(bucket.TotalTokens));
            PublishCounterOnce(bucket, "cached_input_tokens", bucket.CachedInputTokens,  () => _cachedInputTokens.WithLabels(tenant, _provider, model, tenancyId).Inc(bucket.CachedInputTokens));
            PublishCounterOnce(bucket, "requests",            bucket.RequestCount,       () => _requests.WithLabels(tenant, _provider, model, tenancyId).Inc(bucket.RequestCount));
        }
    }

    public void PublishCosts(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        foreach (LlmCostBucket bucket in buckets)
        {
            string tenant = MetricLabelSanitizer.Sanitize(ResolveTenant(bucket.Tenant));
            string model = MetricLabelSanitizer.Sanitize(bucket.Model);
            string tenancyId = MetricLabelSanitizer.Sanitize(bucket.TenancyId);
            double cost = decimal.ToDouble(bucket.CostUsd);

            PublishCounterOnce(bucket, "cost_usd",          cost, () => _costUsd.WithLabels(tenant, _provider, tenancyId).Inc(cost));
            PublishCounterOnce(bucket, "cost_usd_by_model", cost, () => _costUsdByModel.WithLabels(tenant, _provider, model, tenancyId).Inc(cost));
        }
    }

    public void RecordPollSuccess(DateTimeOffset timestamp, TimeSpan duration)
    {
        string tenant = MetricLabelSanitizer.Sanitize(_tenant.Id);
        _pollSuccess.WithLabels(tenant, _provider).Inc();
        _lastSuccessTimestamp.WithLabels(tenant, _provider).Set(timestamp.ToUnixTimeSeconds());
        _lastPollDurationSeconds.WithLabels(tenant, _provider).Set(duration.TotalSeconds);
    }

    public void RecordPollFailure(DateTimeOffset timestamp, TimeSpan duration)
    {
        string tenant = MetricLabelSanitizer.Sanitize(_tenant.Id);
        _pollFailure.WithLabels(tenant, _provider).Inc();
        _lastFailureTimestamp.WithLabels(tenant, _provider).Set(timestamp.ToUnixTimeSeconds());
        _lastPollDurationSeconds.WithLabels(tenant, _provider).Set(duration.TotalSeconds);
    }

    private string ResolveTenant(string bucketTenant)
    {
        if (!string.IsNullOrWhiteSpace(bucketTenant) && bucketTenant != "default")
        {
            return bucketTenant;
        }

        return _tenant.Id;
    }

    private void PublishCounterOnce(LlmUsageBucket bucket, string metricType, double value, Action publish)
    {
        string identity = BuildIdentity(ResolveTenant(bucket.Tenant), bucket.StartTime, bucket.EndTime, bucket.Provider, bucket.Model, bucket.TenancyId, metricType);
        PublishCounterOnce(identity, value, publish);
    }

    private void PublishCounterOnce(LlmCostBucket bucket, string metricType, double value, Action publish)
    {
        string identity = BuildIdentity(ResolveTenant(bucket.Tenant), bucket.StartTime, bucket.EndTime, bucket.Provider, bucket.Model, bucket.TenancyId, metricType);
        PublishCounterOnce(identity, value, publish);
    }

    private void PublishCounterOnce(string identity, double value, Action publish)
    {
        if (!_checkpointStore.TryRecord(identity))
        {
            return;
        }

        if (value > 0)
        {
            publish();
        }
    }

    private static string BuildIdentity(
        string tenant,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string provider,
        string? model,
        string? tenancyId,
        string metricType)
    {
        return string.Join(
            "|",
            tenant,
            provider,
            startTime.ToUnixTimeSeconds(),
            endTime.ToUnixTimeSeconds(),
            MetricLabelSanitizer.Sanitize(model),
            MetricLabelSanitizer.Sanitize(tenancyId),
            metricType);
    }
}
