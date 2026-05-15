// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public sealed class BudgetEvaluator
{
    private readonly object _sync = new();
    private readonly Dictionary<string, BudgetState> _state = new(StringComparer.Ordinal);

    public IReadOnlyList<BudgetEvaluation> Evaluate(
        IReadOnlyCollection<LlmCostBucket> costBuckets,
        IReadOnlyList<BudgetDefinition> budgets,
        DateTimeOffset now)
    {
        if (budgets.Count == 0)
        {
            return Array.Empty<BudgetEvaluation>();
        }

        var results = new List<BudgetEvaluation>(budgets.Count);

        lock (_sync)
        {
            foreach (BudgetDefinition budget in budgets)
            {
                if (string.IsNullOrWhiteSpace(budget.Name))
                {
                    continue;
                }

                DateTimeOffset periodStart = ComputePeriodStart(budget.Period, now);

                if (!_state.TryGetValue(budget.Name, out BudgetState? state) || state.PeriodStart != periodStart)
                {
                    state = new BudgetState(periodStart);
                    _state[budget.Name] = state;
                }

                foreach (LlmCostBucket bucket in costBuckets)
                {
                    if (bucket.StartTime < periodStart)
                    {
                        continue;
                    }

                    if (!Matches(budget, bucket))
                    {
                        continue;
                    }

                    string identity = BuildIdentity(bucket);
                    if (!state.SeenIdentities.Add(identity))
                    {
                        continue;
                    }

                    state.RunningSpend += bucket.CostUsd;
                }

                decimal limit = budget.LimitUsd;
                double burnRatio = limit > 0m
                    ? decimal.ToDouble(state.RunningSpend / limit)
                    : 0.0;

                results.Add(new BudgetEvaluation(
                    budget.Name,
                    state.PeriodStart,
                    state.RunningSpend,
                    limit,
                    burnRatio));
            }
        }

        return results;
    }

    public static DateTimeOffset ComputePeriodStart(string period, DateTimeOffset now)
    {
        DateTimeOffset utcNow = now.ToUniversalTime();
        string normalized = (period ?? string.Empty).Trim().ToLowerInvariant();

        return normalized switch
        {
            "daily" => new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero),
            "weekly" => StartOfWeek(utcNow),
            _ => new DateTimeOffset(utcNow.Year, utcNow.Month, 1, 0, 0, 0, TimeSpan.Zero),
        };
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset utcNow)
    {
        int delta = ((int)utcNow.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        DateTimeOffset start = new(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero);
        return start.AddDays(-delta);
    }

    private static bool Matches(BudgetDefinition budget, LlmCostBucket bucket)
    {
        if (budget.Providers.Length > 0 && !ContainsIgnoreCase(budget.Providers, bucket.Provider))
        {
            return false;
        }

        if (budget.Models.Length > 0 && !ContainsIgnoreCase(budget.Models, bucket.Model))
        {
            return false;
        }

        if (budget.Tenancies.Length > 0 && !ContainsIgnoreCase(budget.Tenancies, bucket.TenancyId))
        {
            return false;
        }

        if (budget.Tenants.Length > 0 && !ContainsIgnoreCase(budget.Tenants, bucket.Tenant))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsIgnoreCase(string[] values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        foreach (string value in values)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildIdentity(LlmCostBucket bucket)
    {
        return string.Join(
            "|",
            bucket.Tenant ?? "default",
            bucket.Provider,
            bucket.StartTime.ToUnixTimeSeconds().ToString(),
            bucket.EndTime.ToUnixTimeSeconds().ToString(),
            bucket.Model ?? string.Empty,
            bucket.TenancyId ?? string.Empty);
    }

    private sealed class BudgetState
    {
        public BudgetState(DateTimeOffset periodStart)
        {
            PeriodStart = periodStart;
        }

        public DateTimeOffset PeriodStart { get; }

        public decimal RunningSpend { get; set; }

        public HashSet<string> SeenIdentities { get; } = new(StringComparer.Ordinal);
    }
}

public sealed record BudgetEvaluation(
    string Name,
    DateTimeOffset PeriodStart,
    decimal Spend,
    decimal Limit,
    double BurnRatio);
