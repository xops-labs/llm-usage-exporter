// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics.Checkpoints;

namespace LlmUsageExporter.Tests;

public sealed class InMemoryCheckpointStoreTests
{
    [Fact]
    public void TryRecord_ReturnsTrueOnFirstInsert()
    {
        var store = new InMemoryCheckpointStore();

        bool added = store.TryRecord("openai|1710000000|1710003600|gpt-test|proj_123|input_tokens");

        Assert.True(added);
    }

    [Fact]
    public void TryRecord_ReturnsFalseOnDuplicate()
    {
        var store = new InMemoryCheckpointStore();
        const string identity = "openai|1710000000|1710003600|gpt-test|proj_123|input_tokens";

        Assert.True(store.TryRecord(identity));
        Assert.False(store.TryRecord(identity));
    }

    [Fact]
    public async Task FlushAsync_IsNoOp()
    {
        var store = new InMemoryCheckpointStore();
        store.TryRecord("identity-1");

        await store.FlushAsync(CancellationToken.None);

        // After flushing, the entry should still dedupe on a repeat record.
        Assert.False(store.TryRecord("identity-1"));
    }
}
