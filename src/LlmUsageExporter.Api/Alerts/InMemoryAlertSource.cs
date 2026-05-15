// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public sealed class InMemoryAlertSource : IAlertSource
{
    private const int MaxBufferSize = 100_000;

    private readonly object _usageSync = new();
    private readonly object _costSync = new();
    private readonly LinkedList<LlmUsageBucket> _usageBuffer = new();
    private readonly LinkedList<LlmCostBucket> _costBuffer = new();

    public void ObserveUsage(IReadOnlyCollection<LlmUsageBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            return;
        }

        lock (_usageSync)
        {
            foreach (LlmUsageBucket bucket in buckets)
            {
                _usageBuffer.AddLast(bucket);
                while (_usageBuffer.Count > MaxBufferSize)
                {
                    _usageBuffer.RemoveFirst();
                }
            }
        }
    }

    public void ObserveCost(IReadOnlyCollection<LlmCostBucket> buckets)
    {
        if (buckets.Count == 0)
        {
            return;
        }

        lock (_costSync)
        {
            foreach (LlmCostBucket bucket in buckets)
            {
                _costBuffer.AddLast(bucket);
                while (_costBuffer.Count > MaxBufferSize)
                {
                    _costBuffer.RemoveFirst();
                }
            }
        }
    }

    public IReadOnlyCollection<LlmUsageBucket> DrainUsage()
    {
        lock (_usageSync)
        {
            if (_usageBuffer.Count == 0)
            {
                return Array.Empty<LlmUsageBucket>();
            }

            LlmUsageBucket[] snapshot = new LlmUsageBucket[_usageBuffer.Count];
            _usageBuffer.CopyTo(snapshot, 0);
            _usageBuffer.Clear();
            return snapshot;
        }
    }

    public IReadOnlyCollection<LlmCostBucket> DrainCost()
    {
        lock (_costSync)
        {
            if (_costBuffer.Count == 0)
            {
                return Array.Empty<LlmCostBucket>();
            }

            LlmCostBucket[] snapshot = new LlmCostBucket[_costBuffer.Count];
            _costBuffer.CopyTo(snapshot, 0);
            _costBuffer.Clear();
            return snapshot;
        }
    }
}
