// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Metrics.Checkpoints;

public interface ICheckpointStore
{
    // Returns true if the identity was newly added; false if it already existed.
    bool TryRecord(string identity);

    // Best-effort flush — called periodically by the publisher and on shutdown.
    Task FlushAsync(CancellationToken cancellationToken);
}
