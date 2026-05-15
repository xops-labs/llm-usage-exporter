// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Metrics.Checkpoints;

namespace LlmUsageExporter.Tests;

public sealed class FileCheckpointStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public FileCheckpointStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "llm-checkpoint-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }

    [Fact]
    public async Task TryRecord_PersistsAcrossInstances()
    {
        string path = Path.Combine(_tempRoot, "checkpoints.jsonl");
        CheckpointStoreOptions options = new()
        {
            Provider = "File",
            FilePath = path,
            RetentionHours = 168,
            MaxEntries = 100,
            FlushIntervalSeconds = 30
        };

        var first = new FileCheckpointStore(options);
        Assert.True(first.TryRecord("identity-a"));
        Assert.True(first.TryRecord("identity-b"));
        Assert.True(first.TryRecord("identity-c"));
        await first.FlushAsync(CancellationToken.None);
        await first.DisposeAsync();

        var second = new FileCheckpointStore(options);
        try
        {
            Assert.False(second.TryRecord("identity-a"));
            Assert.False(second.TryRecord("identity-b"));
            Assert.False(second.TryRecord("identity-c"));
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task Compaction_DiscardsExpiredEntries()
    {
        string path = Path.Combine(_tempRoot, "expiring.jsonl");
        CheckpointStoreOptions options = new()
        {
            Provider = "File",
            FilePath = path,
            RetentionHours = 0,
            MaxEntries = 100,
            FlushIntervalSeconds = 30
        };

        var first = new FileCheckpointStore(options);
        Assert.True(first.TryRecord("expired-a"));
        Assert.True(first.TryRecord("expired-b"));
        await first.FlushAsync(CancellationToken.None);
        await first.DisposeAsync();

        // Ensure the cutoff (now) is strictly greater than the recorded-at unix seconds
        // so the reload path discards the expired entries.
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        var second = new FileCheckpointStore(options);
        try
        {
            // Expired entries were dropped during Load(), so TryRecord re-adds them.
            Assert.True(second.TryRecord("expired-a"));
            Assert.True(second.TryRecord("expired-b"));
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Fact]
    public async Task TryRecord_HandlesMissingDirectoryGracefully()
    {
        string nested = Path.Combine(_tempRoot, "deep", "nested", "missing");
        string path = Path.Combine(nested, "checkpoints.jsonl");
        Assert.False(Directory.Exists(nested));

        CheckpointStoreOptions options = new()
        {
            Provider = "File",
            FilePath = path,
            RetentionHours = 168,
            MaxEntries = 100,
            FlushIntervalSeconds = 30
        };

        var store = new FileCheckpointStore(options);
        try
        {
            Assert.True(Directory.Exists(nested));
            Assert.True(store.TryRecord("ident-1"));
            await store.FlushAsync(CancellationToken.None);
            Assert.True(File.Exists(path));
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
}
