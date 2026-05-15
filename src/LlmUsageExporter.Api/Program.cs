// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Alerts;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Endpoints;
using LlmUsageExporter.Api.Focus;
using LlmUsageExporter.Api.Health;
using LlmUsageExporter.Api.Metrics.Checkpoints;
using LlmUsageExporter.Api.Metrics.Otlp;
using LlmUsageExporter.Api.Providers.Anthropic;
using LlmUsageExporter.Api.Providers.AzureOpenAI;
using LlmUsageExporter.Api.Providers.Bedrock;
using LlmUsageExporter.Api.Providers.Demo;
using LlmUsageExporter.Api.Providers.Gemini;
using LlmUsageExporter.Api.Providers.OpenAI;
using LlmUsageExporter.Api.Tracing;
using LlmUsageExporter.Api.Workers;
using Microsoft.Extensions.Options;
using Prometheus;

// Register a default ActivityListener for our ActivitySource so outbound provider
// calls can propagate W3C trace context by default even when OTLP export is
// disabled. The suppression handler strips propagation headers when configured.
ExporterActivitySource.EnsureListenerRegistered();

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

builder.Services.Configure<ExporterOptions>(builder.Configuration.GetSection(ExporterOptions.SectionName));
builder.Services.PostConfigure<ExporterOptions>(options => ApplyExporterEnvironment(options, builder.Configuration));
builder.Services
    .AddOptions<ExporterOptions>()
    .Validate(options => options.PollIntervalSeconds > 0, "Exporter:PollIntervalSeconds must be greater than zero.")
    .Validate(options => options.LookbackMinutes > 0, "Exporter:LookbackMinutes must be greater than zero.")
    .Validate(options => options.FailureThreshold > 0, "Exporter:FailureThreshold must be greater than zero.")
    .ValidateOnStart();

builder.Services.Configure<TenantsOptions>(builder.Configuration.GetSection(TenantsOptions.SectionName));

builder.Services.AddSingleton<ExporterHealthState>();

builder.Services.AddCheckpointStore(builder.Configuration);
builder.Services.AddOtlpExport(builder.Configuration);

builder.Services.AddOpenAiProvider(builder.Configuration);
builder.Services.AddAzureOpenAiProvider(builder.Configuration);
builder.Services.AddAnthropicProvider(builder.Configuration);
builder.Services.AddGeminiProvider(builder.Configuration);
builder.Services.AddBedrockProvider(builder.Configuration);
builder.Services.AddDemoProvider(builder.Configuration);

builder.Services.AddAlerts(builder.Configuration);
builder.Services.AddFocusExport(builder.Configuration);

builder.Services.AddHostedService<UsagePollingWorker>();

var app = builder.Build();

app.UseHttpMetrics();
app.MapTenantMetrics();
app.MapFocusCsv();
app.MapFocusJson();

app.MapGet("/", () => Results.Ok(new { service = "llm-usage-exporter", metrics = "/metrics", health = "/health", focus = "/focus.json" }));

app.MapGet("/health", (ExporterHealthState healthState, IOptions<ExporterOptions> options) =>
{
    ExporterHealthSnapshot snapshot = healthState.GetSnapshot(options.Value.FailureThreshold);
    return snapshot.Status == "healthy"
        ? Results.Ok(snapshot)
        : Results.Json(snapshot, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.Run();

static void ApplyExporterEnvironment(ExporterOptions options, IConfiguration configuration)
{
    options.PollIntervalSeconds = ReadInt(configuration, "EXPORTER_POLL_INTERVAL_SECONDS", options.PollIntervalSeconds);
    options.LookbackMinutes = ReadInt(configuration, "EXPORTER_LOOKBACK_MINUTES", options.LookbackMinutes);
}

static int ReadInt(IConfiguration configuration, string key, int current)
{
    string? value = configuration[key];
    return int.TryParse(value, out int parsed) ? parsed : current;
}

public partial class Program { }
