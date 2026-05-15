// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Tests;

// Test classes that hold a live MeterListener must run serially.
// MeterListener.InstrumentPublished fires for ALL meters in the process with a matching name,
// so a listener created in one test class will capture measurements emitted by publishers
// created in a concurrently-running test class, causing Assert.Single to fail.
[CollectionDefinition(Name)]
public sealed class MeterListenerCollection
{
    public const string Name = "MeterListenerTests";
}
