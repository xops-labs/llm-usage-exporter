// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Gemini;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Tests;

public sealed class GeminiUsageProviderTests
{
    private const string InputTokenJson = """
    {
      "timeSeries": [
        {
          "metric": {
            "type": "aiplatform.googleapis.com/publisher/online_serving/token_count",
            "labels": { "model_id": "gemini-1.5-pro", "request_type": "input" }
          },
          "resource": {
            "type": "aiplatform.googleapis.com/PublisherModel",
            "labels": { "project_id": "my-proj" }
          },
          "points": [
            {
              "interval": { "startTime": "2024-01-01T00:00:00Z", "endTime": "2024-01-01T01:00:00Z" },
              "value": { "int64Value": "120" }
            }
          ]
        }
      ],
      "nextPageToken": null
    }
    """;

    private const string OutputTokenJson = """
    {
      "timeSeries": [
        {
          "metric": {
            "type": "aiplatform.googleapis.com/publisher/online_serving/token_count",
            "labels": { "model_id": "gemini-1.5-pro", "request_type": "output" }
          },
          "resource": {
            "type": "aiplatform.googleapis.com/PublisherModel",
            "labels": { "project_id": "my-proj" }
          },
          "points": [
            {
              "interval": { "startTime": "2024-01-01T00:00:00Z", "endTime": "2024-01-01T01:00:00Z" },
              "value": { "int64Value": "80" }
            }
          ]
        }
      ],
      "nextPageToken": null
    }
    """;

    private const string RequestCountJson = """
    {
      "timeSeries": [
        {
          "metric": {
            "type": "aiplatform.googleapis.com/publisher/online_serving/request_count",
            "labels": { "model_id": "gemini-1.5-pro" }
          },
          "resource": {
            "type": "aiplatform.googleapis.com/PublisherModel",
            "labels": { "project_id": "my-proj" }
          },
          "points": [
            {
              "interval": { "startTime": "2024-01-01T00:00:00Z", "endTime": "2024-01-01T01:00:00Z" },
              "value": { "int64Value": "5" }
            }
          ]
        }
      ],
      "nextPageToken": null
    }
    """;

    [Fact]
    public async Task GetUsageAsync_MapsCloudMonitoringResponse()
    {
        Dictionary<string, string> responsesByFilter = new(StringComparer.Ordinal)
        {
            ["input"] = InputTokenJson,
            ["output"] = OutputTokenJson,
            ["request"] = RequestCountJson
        };

        GeminiUsageProvider provider = CreateProvider(request => RouteRequest(request, responsesByFilter));

        var buckets = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("gemini", bucket.Provider);
        Assert.Equal("gemini-1.5-pro", bucket.Model);
        Assert.Equal("my-proj", bucket.TenancyId);
        Assert.Equal(120, bucket.InputTokens);
        Assert.Equal(80, bucket.OutputTokens);
        Assert.Equal(200, bucket.TotalTokens);
        Assert.Equal(0, bucket.CachedInputTokens);
        Assert.Equal(5, bucket.RequestCount);
    }

    [Fact]
    public async Task GetCostsAsync_ReturnsEmptyWhenDisabled()
    {
        int callCount = 0;
        GeminiUsageProvider provider = CreateProvider(_ =>
        {
            Interlocked.Increment(ref callCount);
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var buckets = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-31T00:00:00Z"),
            CancellationToken.None);

        Assert.Empty(buckets);
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task GeminiUsageClient_ThrowsForFailedResponses()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":"forbidden"}""", Encoding.UTF8, "application/json")
            }))
        {
            BaseAddress = new Uri("https://monitoring.googleapis.com")
        };

        GeminiUsageClient client = CreateClient(httpClient);
        var query = new GeminiUsageQuery(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            "3600s",
            []);

        GeminiUsageClientException exception = await Assert.ThrowsAsync<GeminiUsageClientException>(() =>
            client.GetInputTokenSeriesAsync(query, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.False(exception.IsTransient);
    }

    private static HttpResponseMessage RouteRequest(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string> responsesByFilter)
    {
        string url = request.RequestUri?.ToString() ?? string.Empty;
        string decoded = Uri.UnescapeDataString(url);

        if (decoded.Contains("request_type = \"input\""))
        {
            return JsonResponse(responsesByFilter["input"]);
        }

        if (decoded.Contains("request_type = \"output\""))
        {
            return JsonResponse(responsesByFilter["output"]);
        }

        if (decoded.Contains("request_count"))
        {
            return JsonResponse(responsesByFilter["request"]);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("unknown filter", Encoding.UTF8, "text/plain")
        };
    }

    private static GeminiUsageProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("https://monitoring.googleapis.com")
        };

        IOptionsMonitor<GeminiOptions> monitor = CreateOptionsMonitor(new GeminiOptions
        {
            ProjectId = "my-proj",
            AccessToken = "test-token",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        var tokenProvider = new GeminiTokenProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            monitor,
            NullLogger<GeminiTokenProvider>.Instance);

        var client = new GeminiUsageClient(httpClient, monitor, tokenProvider, NullLogger<GeminiUsageClient>.Instance);
        return new GeminiUsageProvider(client, monitor);
    }

    private static GeminiUsageClient CreateClient(HttpClient httpClient)
    {
        IOptionsMonitor<GeminiOptions> monitor = CreateOptionsMonitor(new GeminiOptions
        {
            ProjectId = "my-proj",
            AccessToken = "test-token",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        var tokenProvider = new GeminiTokenProvider(
            new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            monitor,
            NullLogger<GeminiTokenProvider>.Instance);

        return new GeminiUsageClient(httpClient, monitor, tokenProvider, NullLogger<GeminiUsageClient>.Instance);
    }

    private static IOptionsMonitor<GeminiOptions> CreateOptionsMonitor(GeminiOptions options)
    {
        return new StaticOptionsMonitor<GeminiOptions>(options);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
