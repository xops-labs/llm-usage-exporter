// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Tests;

public sealed class AnthropicUsageProviderTests
{
    [Fact]
    public async Task GetUsageAsync_MapsUsageBucketsFromAnthropicResponse()
    {
        const string json = """
        {
          "data": [
            {
              "starting_at": "2024-01-01T00:00:00Z",
              "ending_at": "2024-01-01T01:00:00Z",
              "results": [
                {
                  "uncached_input_tokens": 100,
                  "cached_input_tokens": 20,
                  "cache_creation_input_tokens": 5,
                  "output_tokens": 50,
                  "server_tool_use": { "web_search_requests": 0 },
                  "model": "claude-3-5-sonnet-20240620",
                  "workspace_id": "wrkspc_abc",
                  "api_key_id": "apikey_123"
                }
              ]
            }
          ],
          "has_more": false,
          "next_page": null
        }
        """;

        AnthropicUsageProvider provider = CreateProvider(_ => JsonResponse(json));

        var buckets = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("anthropic", bucket.Provider);
        Assert.Equal("claude-3-5-sonnet-20240620", bucket.Model);
        Assert.Equal("wrkspc_abc", bucket.TenancyId);
        Assert.Equal(105, bucket.InputTokens);
        Assert.Equal(50, bucket.OutputTokens);
        Assert.Equal(20, bucket.CachedInputTokens);
        Assert.Equal(155, bucket.TotalTokens);
        Assert.Equal(0, bucket.RequestCount);
    }

    [Fact]
    public async Task GetCostsAsync_MapsCostBucketsFromAnthropicResponse()
    {
        const string json = """
        {
          "data": [
            {
              "starting_at": "2024-01-01T00:00:00Z",
              "ending_at": "2024-01-02T00:00:00Z",
              "results": [
                {
                  "amount": { "amount": "1.2345", "currency": "USD" },
                  "currency": "USD",
                  "workspace_id": "wrkspc_abc",
                  "description": "claude-3-5-sonnet-20240620 input tokens"
                }
              ]
            }
          ],
          "has_more": false,
          "next_page": null
        }
        """;

        AnthropicUsageProvider provider = CreateProvider(_ => JsonResponse(json));

        var buckets = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("anthropic", bucket.Provider);
        Assert.Equal("claude-3-5-sonnet-20240620", bucket.Model);
        Assert.Equal("wrkspc_abc", bucket.TenancyId);
        Assert.Equal(1.2345m, bucket.CostUsd);
    }

    [Fact]
    public async Task AnthropicUsageClient_ThrowsForFailedResponses()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"bad key"}""", Encoding.UTF8, "application/json")
            }))
        {
            BaseAddress = new Uri("https://api.anthropic.com")
        };

        AnthropicUsageClient client = CreateClient(httpClient);
        var query = new AnthropicUsageQuery(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            "1h",
            ["model", "workspace_id"],
            [],
            [],
            [],
            24);

        AnthropicUsageClientException exception = await Assert.ThrowsAsync<AnthropicUsageClientException>(() =>
            client.GetMessagesUsageAsync(query, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.False(exception.IsTransient);
    }

    private static AnthropicUsageProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("https://api.anthropic.com")
        };

        var options = Options.Create(new AnthropicOptions
        {
            AdminApiKey = "test-admin-key",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        var client = new AnthropicUsageClient(httpClient, options, NullLogger<AnthropicUsageClient>.Instance);
        return new AnthropicUsageProvider(client, options);
    }

    private static AnthropicUsageClient CreateClient(HttpClient httpClient)
    {
        var options = Options.Create(new AnthropicOptions
        {
            AdminApiKey = "test-admin-key",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        return new AnthropicUsageClient(httpClient, options, NullLogger<AnthropicUsageClient>.Instance);
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
