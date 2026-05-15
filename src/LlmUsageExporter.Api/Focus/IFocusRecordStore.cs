// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Focus;

public interface IFocusRecordStore
{
    void Append(IReadOnlyCollection<FocusRecord> records);

    IReadOnlyList<FocusRecord> Snapshot();
}
