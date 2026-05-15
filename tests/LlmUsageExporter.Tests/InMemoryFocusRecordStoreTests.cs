// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Focus;

namespace LlmUsageExporter.Tests;

public sealed class InMemoryFocusRecordStoreTests
{
    [Fact]
    public void Append_Then_Snapshot_ReturnsRecords()
    {
        var store = new InMemoryFocusRecordStore(capacity: 10);
        FocusRecord a = MakeRecord("a");
        FocusRecord b = MakeRecord("b");

        store.Append(new[] { a, b });
        IReadOnlyList<FocusRecord> snapshot = store.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("a", snapshot[0].ResourceName);
        Assert.Equal("b", snapshot[1].ResourceName);
    }

    [Fact]
    public void Append_BeyondCap_DropsOldest()
    {
        var store = new InMemoryFocusRecordStore(capacity: 3);
        store.Append(new[] { MakeRecord("a"), MakeRecord("b"), MakeRecord("c") });
        store.Append(new[] { MakeRecord("d"), MakeRecord("e") });

        IReadOnlyList<FocusRecord> snapshot = store.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("c", snapshot[0].ResourceName);
        Assert.Equal("d", snapshot[1].ResourceName);
        Assert.Equal("e", snapshot[2].ResourceName);
    }

    [Fact]
    public void Snapshot_IsImmutable()
    {
        var store = new InMemoryFocusRecordStore(capacity: 10);
        store.Append(new[] { MakeRecord("a") });

        IReadOnlyList<FocusRecord> snapshot = store.Snapshot();
        Assert.IsType<FocusRecord[]>(snapshot);
        FocusRecord[] mutable = (FocusRecord[])snapshot;
        mutable[0] = MakeRecord("hacked");

        IReadOnlyList<FocusRecord> snapshotAfter = store.Snapshot();
        Assert.Single(snapshotAfter);
        Assert.Equal("a", snapshotAfter[0].ResourceName);
    }

    private static FocusRecord MakeRecord(string name)
    {
        return new FocusRecord
        {
            ChargePeriodStart = "2026-01-01T00:00:00+00:00",
            ChargePeriodEnd = "2026-01-02T00:00:00+00:00",
            BillingAccountId = "acct",
            BillingAccountName = "acct",
            ProviderName = "OpenAI",
            PublisherName = "OpenAI",
            InvoiceIssuerName = "OpenAI",
            ServiceName = "OpenAI API",
            ResourceId = $"openai:{name}:acct",
            ResourceName = name,
            ChargeDescription = $"LLM token usage for model {name} on provider openai",
            BilledCost = 1m,
            EffectiveCost = 1m,
            ListCost = 1m,
            ContractedCost = 1m,
            Tags = "{\"llm_provider\":\"openai\",\"llm_model\":\"" + name + "\"}",
            x_provider_native_id = "acct"
        };
    }
}
