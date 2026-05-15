// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Tests;

public sealed class OpenAiUsageProviderTests
{
    [Fact]
    public async Task GetUsageAsync_MapsUsageBucketsFromOpenAiResponse()
    {
        const string json = """
        {
          "object": "page",
          "data": [
            {
              "object": "bucket",
              "start_time": 1710000000,
              "end_time": 1710003600,
              "results": [
                {
                  "object": "organization.usage.completions.result",
                  "input_tokens": 12,
                  "output_tokens": 8,
                  "input_cached_tokens": 3,
                  "num_model_requests": 2,
                  "model": "gpt-test",
                  "project_id": "proj_123"
                }
              ]
            }
          ],
          "has_more": false,
          "next_page": null
        }
        """;

        OpenAiUsageProvider provider = CreateProvider(_ => JsonResponse(json));

        var buckets = await provider.GetUsageAsync(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("openai", bucket.Provider);
        Assert.Equal("gpt-test", bucket.Model);
        Assert.Equal("proj_123", bucket.TenancyId);
        Assert.Equal(12, bucket.InputTokens);
        Assert.Equal(8, bucket.OutputTokens);
        Assert.Equal(20, bucket.TotalTokens);
        Assert.Equal(3, bucket.CachedInputTokens);
        Assert.Equal(2, bucket.RequestCount);
    }

    [Fact]
    public async Task GetCostsAsync_MapsCostBucketsFromOpenAiResponse()
    {
        const string json = """
        {
          "object": "page",
          "data": [
            {
              "object": "bucket",
              "start_time": 1710000000,
              "end_time": 1710086400,
              "results": [
                {
                  "object": "organization.costs.result",
                  "amount": { "value": 1.25, "currency": "usd" },
                  "line_item": "gpt-test",
                  "project_id": "proj_123"
                }
              ]
            }
          ],
          "has_more": false,
          "next_page": null
        }
        """;

        OpenAiUsageProvider provider = CreateProvider(_ => JsonResponse(json));

        var buckets = await provider.GetCostsAsync(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710086400),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("openai", bucket.Provider);
        Assert.Equal("gpt-test", bucket.Model);
        Assert.Equal("proj_123", bucket.TenancyId);
        Assert.Equal(1.25m, bucket.CostUsd);
    }

    [Fact]
    public async Task OpenAiUsageClient_ThrowsForFailedResponses()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"bad key"}""", Encoding.UTF8, "application/json")
            }))
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        OpenAiUsageClient client = CreateClient(httpClient);
        var query = new OpenAiUsageQuery(
            DateTimeOffset.FromUnixTimeSeconds(1710000000),
            DateTimeOffset.FromUnixTimeSeconds(1710003600),
            "1h",
            ["model", "project_id"],
            [],
            [],
            [],
            [],
            24);

        OpenAiUsageClientException exception = await Assert.ThrowsAsync<OpenAiUsageClientException>(() =>
            client.GetCompletionsUsageAsync(query, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.False(exception.IsTransient);
    }

    private static OpenAiUsageProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("https://api.openai.com")
        };

        var options = Options.Create(new OpenAiOptions
        {
            AdminApiKey = "test-admin-key",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        var client = new OpenAiUsageClient(httpClient, options, NullLogger<OpenAiUsageClient>.Instance);
        return new OpenAiUsageProvider(client, options);
    }

    private static OpenAiUsageClient CreateClient(HttpClient httpClient)
    {
        var options = Options.Create(new OpenAiOptions
        {
            AdminApiKey = "test-admin-key",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        return new OpenAiUsageClient(httpClient, options, NullLogger<OpenAiUsageClient>.Instance);
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
}
