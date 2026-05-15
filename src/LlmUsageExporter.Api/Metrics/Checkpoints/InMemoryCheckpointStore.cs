// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Metrics.Checkpoints;

public sealed class InMemoryCheckpointStore : ICheckpointStore
{
    private readonly object _sync = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public bool TryRecord(string identity)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        lock (_sync)
        {
            return _seen.Add(identity);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
