// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Health;

public sealed class ExporterHealthState
{
    private readonly object _sync = new();
    private DateTimeOffset? _lastSuccessTimestamp;
    private DateTimeOffset? _lastFailureTimestamp;
    private string? _lastFailureMessage;
    private int _consecutiveFailures;

    public void RecordSuccess(DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            _lastSuccessTimestamp = timestamp;
            _consecutiveFailures = 0;
        }
    }

    public void RecordFailure(Exception exception, DateTimeOffset timestamp)
    {
        lock (_sync)
        {
            _lastFailureTimestamp = timestamp;
            _lastFailureMessage = exception.Message;
            _consecutiveFailures++;
        }
    }

    public ExporterHealthSnapshot GetSnapshot(int failureThreshold)
    {
        lock (_sync)
        {
            bool healthy = _consecutiveFailures < failureThreshold;

            return new ExporterHealthSnapshot(
                healthy ? "healthy" : "unhealthy",
                _consecutiveFailures,
                failureThreshold,
                _lastSuccessTimestamp,
                _lastFailureTimestamp,
                _lastFailureMessage);
        }
    }
}

public sealed record ExporterHealthSnapshot(
    string Status,
    int ConsecutiveFailures,
    int FailureThreshold,
    DateTimeOffset? LastSuccessTimestamp,
    DateTimeOffset? LastFailureTimestamp,
    string? LastFailureMessage);
