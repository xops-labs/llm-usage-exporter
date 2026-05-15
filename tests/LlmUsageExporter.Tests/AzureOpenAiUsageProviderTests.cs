// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.AzureOpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Tests;

public sealed class AzureOpenAiUsageProviderTests
{
    private const string AccountResourceId = "subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/aoai-1";

    [Fact]
    public async Task GetUsageAsync_MapsAzureMetricsResponse()
    {
        const string metricsJson = """
        {
          "value": [
            {
              "name": { "value": "ProcessedPromptTokens", "localizedValue": "Processed Prompt Tokens" },
              "unit": "Count",
              "timeseries": [
                {
                  "metadatavalues": [
                    { "name": { "value": "ModelDeploymentName" }, "value": "gpt-4o-deploy" }
                  ],
                  "data": [
                    { "timeStamp": "2024-03-09T00:00:00Z", "total": 123 }
                  ]
                }
              ]
            }
          ]
        }
        """;

        const string tokenJson = """{"access_token":"fake-token","token_type":"Bearer","expires_in":3600}""";

        AzureOpenAiUsageProvider provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(tokenJson);
            }

            return JsonResponse(metricsJson);
        });

        var buckets = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-03-09T00:00:00Z"),
            DateTimeOffset.Parse("2024-03-09T01:00:00Z"),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("azure_openai", bucket.Provider);
        Assert.Equal("gpt-4o-deploy", bucket.Model);
        Assert.Equal(AccountResourceId, bucket.TenancyId);
        Assert.Equal(123, bucket.InputTokens);
        Assert.Equal(0, bucket.OutputTokens);
        Assert.Equal(123, bucket.TotalTokens);
        Assert.Equal(0, bucket.CachedInputTokens);
        Assert.Equal(0, bucket.RequestCount);
    }

    [Fact]
    public async Task GetCostsAsync_MapsAzureCostQueryResponse()
    {
        const string costJson = """
        {
          "properties": {
            "columns": [
              { "name": "Cost", "type": "Number" },
              { "name": "UsageDate", "type": "Number" },
              { "name": "ResourceId", "type": "String" },
              { "name": "Currency", "type": "String" }
            ],
            "rows": [
              [ 4.5, 20240309, "subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.CognitiveServices/accounts/aoai-1", "USD" ]
            ]
          }
        }
        """;

        const string tokenJson = """{"access_token":"fake-token","token_type":"Bearer","expires_in":3600}""";

        AzureOpenAiUsageProvider provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(tokenJson);
            }

            return JsonResponse(costJson);
        });

        var buckets = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-03-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-03-31T00:00:00Z"),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("azure_openai", bucket.Provider);
        Assert.Equal("aoai-1", bucket.Model);
        Assert.Equal(AccountResourceId, bucket.TenancyId);
        Assert.Equal(4.5m, bucket.CostUsd);
    }

    [Fact]
    public async Task AzureOpenAiUsageClient_ThrowsForFailedResponses()
    {
        const string tokenJson = """{"access_token":"fake-token","token_type":"Bearer","expires_in":3600}""";

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(tokenJson, Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"error":"forbidden"}""", Encoding.UTF8, "application/json")
            };
        }));

        AzureOpenAiUsageClient client = CreateClient(httpClient);
        var query = new AzureOpenAiUsageQuery(
            DateTimeOffset.Parse("2024-03-09T00:00:00Z"),
            DateTimeOffset.Parse("2024-03-09T01:00:00Z"),
            [AccountResourceId],
            [],
            "PT1H");

        AzureOpenAiUsageClientException exception = await Assert.ThrowsAsync<AzureOpenAiUsageClientException>(() =>
            client.GetMetricsAsync(AccountResourceId, query, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task AzureOpenAiUsageClient_UsesClientCredentialsTokenAndCachesIt()
    {
        List<string> tokenRequestBodies = [];
        List<string?> managementAuthorizationHeaders = [];

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase))
            {
                tokenRequestBodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"access_token":"token-1","token_type":"Bearer","expires_in":3600}""");
            }

            managementAuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return JsonResponse("""{"value":[]}""");
        }));

        AzureOpenAiUsageClient client = CreateClient(httpClient);
        var query = new AzureOpenAiUsageQuery(
            DateTimeOffset.Parse("2024-03-09T00:00:00Z"),
            DateTimeOffset.Parse("2024-03-09T01:00:00Z"),
            [AccountResourceId],
            [],
            "PT1H");

        await client.GetMetricsAsync(AccountResourceId, query, CancellationToken.None);
        await client.GetMetricsAsync(AccountResourceId, query, CancellationToken.None);

        string tokenBody = Assert.Single(tokenRequestBodies);
        Assert.Contains("grant_type=client_credentials", tokenBody);
        Assert.Contains("client_id=client-1", tokenBody);
        Assert.Contains("client_secret=secret-1", tokenBody);
        Assert.Contains("scope=https%3A%2F%2Fmanagement.azure.com%2F.default", tokenBody);
        Assert.Equal(["Bearer token-1", "Bearer token-1"], managementAuthorizationHeaders);
    }

    [Fact]
    public async Task AzureOpenAiUsageClient_RefreshesTokenAfterUnauthorizedManagementResponse()
    {
        int tokenCallCount = 0;
        int managementCallCount = 0;
        List<string?> managementAuthorizationHeaders = [];

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase))
            {
                tokenCallCount++;
                return JsonResponse($$"""{"access_token":"token-{{tokenCallCount}}","token_type":"Bearer","expires_in":3600}""");
            }

            managementCallCount++;
            managementAuthorizationHeaders.Add(request.Headers.Authorization?.ToString());

            return managementCallCount == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"expired"}""", Encoding.UTF8, "application/json")
                }
                : JsonResponse("""{"value":[]}""");
        }));

        AzureOpenAiUsageClient client = CreateClient(httpClient);
        var query = new AzureOpenAiUsageQuery(
            DateTimeOffset.Parse("2024-03-09T00:00:00Z"),
            DateTimeOffset.Parse("2024-03-09T01:00:00Z"),
            [AccountResourceId],
            [],
            "PT1H");

        await Assert.ThrowsAsync<AzureOpenAiUsageClientException>(() =>
            client.GetMetricsAsync(AccountResourceId, query, CancellationToken.None));
        await client.GetMetricsAsync(AccountResourceId, query, CancellationToken.None);

        Assert.Equal(2, tokenCallCount);
        Assert.Equal(["Bearer token-1", "Bearer token-2"], managementAuthorizationHeaders);
    }

    private static AzureOpenAiUsageProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory));

        var options = Options.Create(new AzureOpenAiOptions
        {
            TenantId = "tenant-1",
            ClientId = "client-1",
            ClientSecret = "secret-1",
            SubscriptionId = "sub-1",
            ResourceGroup = "rg-1",
            AccountResourceIds = [AccountResourceId],
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        var client = new AzureOpenAiUsageClient(httpClient, options, NullLogger<AzureOpenAiUsageClient>.Instance);
        return new AzureOpenAiUsageProvider(client, options);
    }

    private static AzureOpenAiUsageClient CreateClient(HttpClient httpClient)
    {
        var options = Options.Create(new AzureOpenAiOptions
        {
            TenantId = "tenant-1",
            ClientId = "client-1",
            ClientSecret = "secret-1",
            SubscriptionId = "sub-1",
            AccountResourceIds = [AccountResourceId],
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        return new AzureOpenAiUsageClient(httpClient, options, NullLogger<AzureOpenAiUsageClient>.Instance);
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
