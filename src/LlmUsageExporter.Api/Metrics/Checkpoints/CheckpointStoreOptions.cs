// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Metrics.Checkpoints;

public sealed class CheckpointStoreOptions
{
    public const string SectionName = "Checkpoints";

    // "InMemory" | "File"
    public string Provider { get; set; } = "InMemory";

    // File path for FileCheckpointStore. Default: "./data/checkpoints.jsonl"
    public string FilePath { get; set; } = "./data/checkpoints.jsonl";

    // Cap on entries kept in the in-memory hash + on disk.
    public int MaxEntries { get; set; } = 1_000_000;

    // Discard entries older than this many hours during compaction.
    public int RetentionHours { get; set; } = 168; // 7 days

    // How often to flush write-ahead buffer to disk.
    public int FlushIntervalSeconds { get; set; } = 30;
}
