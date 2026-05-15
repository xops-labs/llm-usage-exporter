// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlmUsageExporter.Api.Metrics.Checkpoints;

public sealed class FileCheckpointStore : ICheckpointStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly CheckpointStoreOptions _options;
    private readonly ILogger<FileCheckpointStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Identity -> recorded-at unix seconds.
    private readonly Dictionary<string, long> _entries = new(StringComparer.Ordinal);
    private readonly List<CheckpointRecord> _pending = new();

    private long _writesSinceCompaction;
    private bool _disposed;

    public FileCheckpointStore(CheckpointStoreOptions options)
        : this(options, NullLogger<FileCheckpointStore>.Instance)
    {
    }

    public FileCheckpointStore(CheckpointStoreOptions options, ILogger<FileCheckpointStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        EnsureDirectoryExists(_options.FilePath);
        Load();
    }

    public bool TryRecord(string identity)
    {
        if (identity is null)
        {
            throw new ArgumentNullException(nameof(identity));
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _gate.Wait();
        try
        {
            if (_entries.ContainsKey(identity))
            {
                return false;
            }

            _entries[identity] = now;
            _pending.Add(new CheckpointRecord(identity, now));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return;
        }

        List<CheckpointRecord> drained;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pending.Count == 0 && _writesSinceCompaction < _options.MaxEntries)
            {
                return;
            }

            drained = new List<CheckpointRecord>(_pending);
            _pending.Clear();
            _writesSinceCompaction += drained.Count;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (drained.Count > 0)
            {
                await AppendAsync(drained, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append checkpoint records to {FilePath}.", _options.FilePath);
        }

        if (_writesSinceCompaction >= _options.MaxEntries)
        {
            try
            {
                await CompactAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compact checkpoint file {FilePath}.", _options.FilePath);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed final checkpoint flush for {FilePath}.", _options.FilePath);
        }

        _disposed = true;
        _gate.Dispose();
    }

    private void Load()
    {
        string path = _options.FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        long cutoff = ComputeCutoff();

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using StreamReader reader = new(stream, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                CheckpointRecord? record = TryParse(line);
                if (record is null)
                {
                    continue;
                }

                if (cutoff > 0 && record.At < cutoff)
                {
                    continue;
                }

                _entries[record.Id] = record.At;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load checkpoint file {FilePath}; continuing empty.", path);
        }
    }

    private async Task AppendAsync(List<CheckpointRecord> records, CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        foreach (CheckpointRecord record in records)
        {
            builder.Append(JsonSerializer.Serialize(record, JsonOptions));
            builder.Append('\n');
        }

        await using FileStream stream = new(
            _options.FilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        long cutoff = ComputeCutoff();
        List<string> lines;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Drop expired entries from the in-memory state too.
            if (cutoff > 0)
            {
                List<string> expired = _entries
                    .Where(kvp => kvp.Value < cutoff)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (string key in expired)
                {
                    _entries.Remove(key);
                }
            }

            // Enforce MaxEntries by keeping the newest entries.
            if (_entries.Count > _options.MaxEntries)
            {
                List<string> overflow = _entries
                    .OrderBy(kvp => kvp.Value)
                    .Take(_entries.Count - _options.MaxEntries)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (string key in overflow)
                {
                    _entries.Remove(key);
                }
            }

            lines = _entries
                .Select(kvp => JsonSerializer.Serialize(new CheckpointRecord(kvp.Key, kvp.Value), JsonOptions))
                .ToList();

            _writesSinceCompaction = 0;
        }
        finally
        {
            _gate.Release();
        }

        string tempPath = _options.FilePath + ".tmp";
        await File.WriteAllLinesAsync(tempPath, lines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        if (File.Exists(_options.FilePath))
        {
            File.Replace(tempPath, _options.FilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(tempPath, _options.FilePath);
        }
    }

    private long ComputeCutoff()
    {
        if (_options.RetentionHours <= 0)
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return DateTimeOffset.UtcNow
            .AddHours(-_options.RetentionHours)
            .ToUnixTimeSeconds();
    }

    private static void EnsureDirectoryExists(string filePath)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static CheckpointRecord? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<CheckpointRecord>(line, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private sealed record CheckpointRecord(
        [property: System.Text.Json.Serialization.JsonPropertyName("id")] string Id,
        [property: System.Text.Json.Serialization.JsonPropertyName("at")] long At);
}
