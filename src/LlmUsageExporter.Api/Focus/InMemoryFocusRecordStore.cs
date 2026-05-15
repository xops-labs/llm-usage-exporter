// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Focus;

public sealed class InMemoryFocusRecordStore : IFocusRecordStore
{
    private readonly object _sync = new();
    private readonly LinkedList<FocusRecord> _records = new();
    private readonly int _capacity;

    public InMemoryFocusRecordStore(IOptions<FocusOptions> options)
        : this(Math.Max(1, options.Value.MaxRecords))
    {
    }

    public InMemoryFocusRecordStore(int capacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public void Append(IReadOnlyCollection<FocusRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            foreach (FocusRecord record in records)
            {
                _records.AddLast(record);
                while (_records.Count > _capacity)
                {
                    _records.RemoveFirst();
                }
            }
        }
    }

    public IReadOnlyList<FocusRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.ToArray();
        }
    }
}
