// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.RegularExpressions;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Tracing;

namespace LlmUsageExporter.Tests;

public sealed class TraceContextPropagationTests
{
    private static readonly Regex TraceparentPattern = new(
        "^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$",
        RegexOptions.Compiled);

    [Fact]
    public void ExporterActivitySource_EmitsActivities_AfterListenerRegistered()
    {
        ExporterActivitySource.EnsureListenerRegistered();

        using Activity? activity = ExporterActivitySource.Instance.StartActivity("test-span");

        Assert.NotNull(activity);
        Assert.True(activity!.IsAllDataRequested);
        Assert.NotEqual(default, activity.TraceId);
        Assert.NotEqual(default, activity.SpanId);
    }

    [Fact]
    public void W3CPropagator_InjectsValidTraceparent_FromExporterActivity()
    {
        // This is exactly the contract HttpClient's SocketsHttpHandler uses when it
        // serializes Activity.Current onto outbound requests via DistributedContextPropagator.Current.
        // If our ActivitySource produces a valid Activity, .NET will propagate traceparent.
        ExporterActivitySource.EnsureListenerRegistered();

        using Activity? activity = ExporterActivitySource.Instance.StartActivity("poll openai", ActivityKind.Client);
        Assert.NotNull(activity);
        activity!.TraceStateString = "vendor=test-state";

        Dictionary<string, string> headers = new();
        DistributedContextPropagator.Current.Inject(
            activity,
            headers,
            static (carrier, key, value) =>
            {
                Dictionary<string, string> dict = (Dictionary<string, string>)carrier!;
                dict[key] = value;
            });

        Assert.True(headers.TryGetValue("traceparent", out string? traceparent));
        Assert.Matches(TraceparentPattern, traceparent!);
        Assert.Contains(activity.TraceId.ToString(), traceparent);
        Assert.Contains(activity.SpanId.ToString(), traceparent);

        Assert.True(headers.TryGetValue("tracestate", out string? tracestate));
        Assert.Equal("vendor=test-state", tracestate);
    }

    [Fact]
    public void ExporterActivitySource_SetsActivityCurrent_DuringScope()
    {
        ExporterActivitySource.EnsureListenerRegistered();

        Activity? before = Activity.Current;

        using (Activity? activity = ExporterActivitySource.Instance.StartActivity("poll bedrock"))
        {
            Assert.NotNull(activity);
            Assert.Same(activity, Activity.Current);
        }

        Assert.Same(before, Activity.Current);
    }

    // ── TraceContextSuppressionHandler tests ───────────────────────────────────

    [Fact]
    public async Task SuppressionHandler_PropagationEnabled_PassesActivityCurrentToInnerHandler()
    {
        ExporterActivitySource.EnsureListenerRegistered();

        OtlpOptions options = new() { TracePropagationEnabled = true };
        Activity? activitySeenByInner = null;

        HttpClient client = BuildTestClient(options, captureActivityCurrent: a => activitySeenByInner = a);

        using Activity? outerActivity = ExporterActivitySource.Instance.StartActivity("poll test");
        Assert.NotNull(outerActivity);

        await client.GetAsync("http://localhost/test");

        // Inner handler saw the same activity — DiagnosticsHandler would inject traceparent.
        Assert.Same(outerActivity, activitySeenByInner);
    }

    [Fact]
    public async Task SuppressionHandler_PropagationDisabled_ClearsActivityCurrentForInnerHandler()
    {
        ExporterActivitySource.EnsureListenerRegistered();

        OtlpOptions options = new() { TracePropagationEnabled = false };
        Activity? activitySeenByInner = new Activity("sentinel"); // non-null sentinel

        HttpClient client = BuildTestClient(options, captureActivityCurrent: a => activitySeenByInner = a);

        using Activity? outerActivity = ExporterActivitySource.Instance.StartActivity("poll test");
        Assert.NotNull(outerActivity);

        await client.GetAsync("http://localhost/test");

        // Inner handler saw null — DiagnosticsHandler would skip header injection.
        Assert.Null(activitySeenByInner);
    }

    [Fact]
    public async Task SuppressionHandler_PropagationDisabled_RestoresActivityCurrentAfterCall()
    {
        ExporterActivitySource.EnsureListenerRegistered();

        OtlpOptions options = new() { TracePropagationEnabled = false };
        HttpClient client = BuildTestClient(options, captureActivityCurrent: _ => { });

        using Activity? outerActivity = ExporterActivitySource.Instance.StartActivity("poll test");
        Assert.NotNull(outerActivity);

        await client.GetAsync("http://localhost/test");

        // After the call, Activity.Current is restored to the outer span.
        Assert.Same(outerActivity, Activity.Current);
    }

    /// <summary>
    /// Builds an HttpClient with <see cref="TraceContextSuppressionHandler"/> as the outermost
    /// handler and a capturing stub as the innermost handler. The stub records
    /// <see cref="Activity.Current"/> at the moment the request reaches the network layer.
    /// </summary>
    private static HttpClient BuildTestClient(OtlpOptions options, Action<Activity?> captureActivityCurrent)
    {
        CapturingHandler inner = new(captureActivityCurrent);
        TraceContextSuppressionHandler suppressor = new(options) { InnerHandler = inner };
        return new HttpClient(suppressor);
    }

    private sealed class CapturingHandler(Action<Activity?> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            capture(Activity.Current);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
