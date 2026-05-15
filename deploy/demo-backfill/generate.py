"""Generate 7 days of synthetic demo-provider data in OpenMetrics format.

Output is written to the path given as the first arg (default: /tmp/backfill.openmetrics).
The file is consumed by `promtool tsdb create-blocks-from openmetrics` so the shipped
Grafana dashboards have realistic-looking history the moment Prometheus starts.

Metric shape mirrors what `DemoUsageProvider` emits at runtime: same metric names,
same label keys, same provider/tenant/model surface. The values use a diurnal +
weekly pattern with small jitter so the dashboards look operational, not flat.
"""
from __future__ import annotations

import math
import os
import random
import sys
import time

END_TS = int(time.time()) - 60
START_TS = END_TS - 7 * 86400
INTERVAL_S = 300

TENANTS = [
    ("demo-team-alpha", "proj_demo_alpha"),
    ("demo-team-beta",  "proj_demo_beta"),
]
MODELS = [
    "demo-flagship-large",
    "demo-flagship-mini",
    "demo-fast-haiku",
    "demo-fast-mini",
    "demo-multimodal",
]
MODEL_PRICE_PER_1K = {
    "demo-flagship-large": 0.020,
    "demo-flagship-mini":  0.001,
    "demo-fast-haiku":     0.00050,
    "demo-fast-mini":      0.00030,
    "demo-multimodal":     0.010,
}
TENANT_SCALE = {"demo-team-alpha": 1.0, "demo-team-beta": 0.55}
MODEL_WEIGHT = {
    "demo-flagship-large": 0.30,
    "demo-flagship-mini":  0.25,
    "demo-fast-haiku":     0.20,
    "demo-fast-mini":      0.15,
    "demo-multimodal":     0.10,
}

random.seed(42)


def diurnal(ts: int) -> float:
    hour = (ts // 3600) % 24
    base = 0.55 + 0.5 * math.cos((hour - 14) * math.pi / 12)
    if time.gmtime(ts).tm_wday >= 5:
        base *= 0.6
    return max(0.05, base)


def lbl_model(tenant: str, tid: str, model: str) -> str:
    return f'tenant="{tenant}",provider="demo",model="{model}",tenancy_id="{tid}"'


def lbl_tenant(tenant: str, tid: str) -> str:
    return f'tenant="{tenant}",provider="demo",tenancy_id="{tid}"'


series: dict[str, dict[str, list[tuple[int, float]]]] = {}
cumulative: dict[tuple[str, str], float] = {}


def add(metric: str, labels: str, ts: int, value: float) -> None:
    series.setdefault(metric, {}).setdefault(labels, []).append((ts, value))


for ts in range(START_TS, END_TS, INTERVAL_S):
    d = diurnal(ts)
    for tenant, tid in TENANTS:
        scale = TENANT_SCALE[tenant]
        tenant_cost_inc = 0.0
        for model in MODELS:
            w = MODEL_WEIGHT[model]
            base_inp = (1200 + 4500 * d) * scale * w
            inp_inc = max(1, int(base_inp * (1 + random.uniform(-0.18, 0.22))))
            out_inc = max(1, int(inp_inc * 0.38 * (1 + random.uniform(-0.10, 0.10))))
            cached_inc = max(0, int(inp_inc * 0.18))
            req_inc = max(1, int(inp_inc / 220))
            tot_inc = inp_inc + out_inc
            cost_inc = (inp_inc + out_inc) / 1000.0 * MODEL_PRICE_PER_1K[model]
            tenant_cost_inc += cost_inc

            lbl = lbl_model(tenant, tid, model)
            for metric, inc in [
                ("llm_usage_input_tokens_total",        inp_inc),
                ("llm_usage_output_tokens_total",       out_inc),
                ("llm_usage_total_tokens_total",        tot_inc),
                ("llm_usage_cached_input_tokens_total", cached_inc),
                ("llm_usage_requests_total",            req_inc),
                ("llm_usage_cost_usd_by_model_total",   cost_inc),
            ]:
                key = (metric, lbl)
                cumulative[key] = cumulative.get(key, 0) + inc
                add(metric, lbl, ts, cumulative[key])

        lbl = lbl_tenant(tenant, tid)
        key = ("llm_usage_cost_usd_total", lbl)
        cumulative[key] = cumulative.get(key, 0) + tenant_cost_inc
        add("llm_usage_cost_usd_total", lbl, ts, cumulative[key])

poll_lbl = 'tenant="default",provider="demo"'
poll_count = 0
for ts in range(START_TS, END_TS, INTERVAL_S):
    poll_count += 1
    add("llm_exporter_poll_success_total",         poll_lbl, ts, poll_count)
    add("llm_exporter_poll_failure_total",         poll_lbl, ts, 0)
    add("llm_exporter_last_success_timestamp",     poll_lbl, ts, ts)
    add("llm_exporter_last_poll_duration_seconds", poll_lbl, ts, round(0.0008 + random.uniform(0, 0.004), 6))

# Alert + anomaly metrics. These would normally be emitted live by the
# alerts subsystem when ALERTS_ENABLED=true and a budget is configured; the
# demo synthesizes plausible historical values so the Cost & Budgets dashboard
# is populated end-to-end on first open.
BUDGET_LIMIT_USD = 50.0
BUDGET_PERIOD_START = START_TS
for ts in range(START_TS, END_TS, INTERVAL_S):
    elapsed = ts - START_TS
    spend = round(110 * (elapsed / (7 * 86400)) * (1 + random.uniform(-0.05, 0.05)), 4)
    burn  = round(spend / BUDGET_LIMIT_USD, 4)
    add("llm_alerts_budget_spend_usd",             'budget="demo_weekly_budget",tenant="default"', ts, spend)
    add("llm_alerts_budget_limit_usd",             'budget="demo_weekly_budget",tenant="default"', ts, BUDGET_LIMIT_USD)
    add("llm_alerts_budget_burn_ratio",            'budget="demo_weekly_budget",tenant="default"', ts, burn)
    add("llm_alerts_budget_period_start_timestamp",'budget="demo_weekly_budget",tenant="default"', ts, BUDGET_PERIOD_START)

# Cost + token anomaly z-scores: small noise around zero with a few mild spikes
# so the timeseries shows movement without falsely implying real anomalies.
for ts in range(START_TS, END_TS, INTERVAL_S):
    hour = (ts // 3600) % 24
    base = 0.3 * math.sin((ts // INTERVAL_S) * 0.18)
    spike = 1.4 if (hour == 11 and random.random() < 0.05) else 0.0
    for tenant, tid in TENANTS:
        for model in MODELS:
            cost_z  = round(base + spike + random.uniform(-0.35, 0.35), 3)
            token_z = round(base * 0.6 + random.uniform(-0.30, 0.30), 3)
            ml = lbl_model(tenant, tid, model)
            add("llm_alerts_cost_anomaly_score",  ml, ts, cost_z)
            add("llm_alerts_token_anomaly_score", ml, ts, token_z)

# OTel Collector exporter metrics. The compose stack doesn't run an OTel
# Collector — these would normally come from one sitting between the exporter
# and the downstream observability backend. Synthesizing them keeps the
# Exporter Health dashboard's OTLP panels populated and gives operators a
# preview of what the surface looks like under load.
for otlp_exporter in ("otlphttp/managed", "prometheusremotewrite"):
    lbl = f'exporter="{otlp_exporter}"'
    for ts in range(START_TS, END_TS, INTERVAL_S):
        hour = (ts // 3600) % 24
        # Queue usage drifts with diurnal load; busy hours push it higher.
        load = 0.55 + 0.5 * math.cos((hour - 14) * math.pi / 12)
        queue_size = max(0, int(load * 80 + random.uniform(-15, 15)))
        add("otelcol_exporter_queue_size",     lbl, ts, queue_size)
        add("otelcol_exporter_queue_capacity", lbl, ts, 1000)
    # Counter-style failures: zero most of the time with rare blips
    fail_count = 0
    enqueue_count = 0
    for ts in range(START_TS, END_TS, INTERVAL_S):
        if random.random() < 0.005:
            fail_count += random.randint(1, 8)
        if random.random() < 0.003:
            enqueue_count += random.randint(1, 4)
        add("otelcol_exporter_send_failed_metric_points_total",    lbl, ts, fail_count)
        add("otelcol_exporter_enqueue_failed_metric_points_total", lbl, ts, enqueue_count)

HELP_TYPE = {
    "llm_usage_input_tokens_total":            ("Aggregated LLM input tokens.",                "counter"),
    "llm_usage_output_tokens_total":           ("Aggregated LLM output tokens.",               "counter"),
    "llm_usage_total_tokens_total":            ("Aggregated LLM total tokens.",                "counter"),
    "llm_usage_cached_input_tokens_total":     ("Aggregated cached input tokens.",             "counter"),
    "llm_usage_requests_total":                ("Aggregated LLM request count.",               "counter"),
    "llm_usage_cost_usd_total":                ("Aggregated USD cost per tenant.",             "counter"),
    "llm_usage_cost_usd_by_model_total":       ("Aggregated USD cost per (tenant, model).",    "counter"),
    "llm_exporter_poll_success_total":         ("Successful poll cycles.",                     "counter"),
    "llm_exporter_poll_failure_total":         ("Failed poll cycles.",                         "counter"),
    "llm_exporter_last_success_timestamp":     ("Unix timestamp of the last successful poll.", "gauge"),
    "llm_exporter_last_poll_duration_seconds": ("Duration of the most recent poll.",           "gauge"),
    "llm_alerts_budget_spend_usd":              ("Running spend in USD for the budget period.","gauge"),
    "llm_alerts_budget_limit_usd":              ("Configured budget spending limit in USD.",   "gauge"),
    "llm_alerts_budget_burn_ratio":             ("Budget burn ratio (spend / limit).",         "gauge"),
    "llm_alerts_budget_period_start_timestamp": ("Start timestamp of the current budget period.", "gauge"),
    "llm_alerts_cost_anomaly_score":            ("Z-score of cost vs rolling baseline.",       "gauge"),
    "llm_alerts_token_anomaly_score":           ("Z-score of token throughput vs baseline.",   "gauge"),
    "otelcol_exporter_queue_size":              ("OpenTelemetry Collector exporter queue size.", "gauge"),
    "otelcol_exporter_queue_capacity":          ("OpenTelemetry Collector exporter queue capacity.", "gauge"),
    "otelcol_exporter_send_failed_metric_points_total":    ("OTel Collector failed send count.",    "counter"),
    "otelcol_exporter_enqueue_failed_metric_points_total": ("OTel Collector failed enqueue count.", "counter"),
}

out_path = sys.argv[1] if len(sys.argv) > 1 else "/tmp/backfill.openmetrics"
n_samples = 0
with open(out_path, "w", newline="\n") as f:
    for metric, by_labels in series.items():
        help_text, type_text = HELP_TYPE[metric]
        family = metric[:-len("_total")] if type_text == "counter" and metric.endswith("_total") else metric
        f.write(f"# HELP {family} {help_text}\n")
        f.write(f"# TYPE {family} {type_text}\n")
        for lbl in sorted(by_labels):
            for ts, val in by_labels[lbl]:
                if isinstance(val, int):
                    f.write(f"{metric}{{{lbl}}} {val} {ts}\n")
                else:
                    f.write(f"{metric}{{{lbl}}} {val:.6f} {ts}\n")
                n_samples += 1
    f.write("# EOF\n")

print(f"wrote {n_samples:,} samples spanning {(END_TS - START_TS) / 86400:.1f} days to {out_path}", flush=True)
