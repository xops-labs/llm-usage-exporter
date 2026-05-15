// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Providers.Bedrock;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Tests;

public sealed class BedrockUsageProviderTests
{
    [Fact]
    public async Task GetUsageAsync_MapsCloudWatchMetricData()
    {
        const string timestamp = "2024-01-01T00:00:00Z";

        string MetricXml(string metricName, double value) => $"""
        <?xml version="1.0"?>
        <GetMetricDataResponse xmlns="http://monitoring.amazonaws.com/doc/2010-08-01/">
          <GetMetricDataResult>
            <MetricDataResults>
              <member>
                <Id>m1</Id>
                <Label>anthropic.claude-3-5-sonnet-20240620-v1:0</Label>
                <Timestamps>
                  <member>{timestamp}</member>
                </Timestamps>
                <Values>
                  <member>{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}</member>
                </Values>
                <StatusCode>Complete</StatusCode>
              </member>
            </MetricDataResults>
          </GetMetricDataResult>
          <ResponseMetadata><RequestId>req-{metricName}</RequestId></ResponseMetadata>
        </GetMetricDataResponse>
        """;

        int callCount = 0;
        var provider = CreateProvider(
            options =>
            {
                options.Region = "us-east-1";
                options.MetricsPeriodSeconds = 3600;
                options.ModelIds = ["anthropic.claude-3-5-sonnet-20240620-v1:0"];
                options.EnableCostQueries = false;
            },
            request =>
            {
                callCount++;
                string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                string xml;
                if (body.Contains("InputTokenCount"))
                {
                    xml = MetricXml("InputTokenCount", 120);
                }
                else if (body.Contains("OutputTokenCount"))
                {
                    xml = MetricXml("OutputTokenCount", 80);
                }
                else
                {
                    xml = MetricXml("Invocations", 4);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "text/xml"),
                };
            });

        var buckets = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            CancellationToken.None);

        var bucket = Assert.Single(buckets);
        Assert.Equal("bedrock", bucket.Provider);
        Assert.Equal("anthropic.claude-3-5-sonnet-20240620-v1:0", bucket.Model);
        Assert.Equal("us-east-1", bucket.TenancyId);
        Assert.Equal(120, bucket.InputTokens);
        Assert.Equal(80, bucket.OutputTokens);
        Assert.Equal(200, bucket.TotalTokens);
        Assert.Equal(0, bucket.CachedInputTokens);
        Assert.Equal(4, bucket.RequestCount);
        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task GetCostsAsync_MapsCostExplorerResponse()
    {
        const string json = """
        {
          "ResultsByTime": [
            {
              "TimePeriod": { "Start": "2024-01-01", "End": "2024-01-02" },
              "Groups": [
                {
                  "Keys": ["USE1-Tokens-Anthropic-Claude-3-5-Sonnet-input"],
                  "Metrics": {
                    "UnblendedCost": { "Amount": "1.23", "Unit": "USD" }
                  }
                },
                {
                  "Keys": ["USE1-Bedrock-Tokens-meta-llama3-input"],
                  "Metrics": {
                    "UnblendedCost": { "Amount": "0.50", "Unit": "USD" }
                  }
                }
              ]
            }
          ]
        }
        """;

        var provider = CreateProvider(
            options =>
            {
                options.Region = "us-east-1";
                options.EnableCostQueries = true;
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/x-amz-json-1.1"),
            });

        var buckets = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-02T00:00:00Z"),
            CancellationToken.None);

        Assert.Equal(2, buckets.Count);
        var byCost = buckets.OrderByDescending(bucket => bucket.CostUsd).ToList();
        Assert.Equal("bedrock", byCost[0].Provider);
        Assert.Equal("us-east-1", byCost[0].TenancyId);
        Assert.Equal(1.23m, byCost[0].CostUsd);
        Assert.NotNull(byCost[0].Model);
        // Model hint should match one of the known Bedrock provider/model fragments.
        string firstModel = byCost[0].Model!;
        Assert.True(
            firstModel.Contains("Anthropic", StringComparison.OrdinalIgnoreCase)
                || firstModel.Contains("Claude", StringComparison.OrdinalIgnoreCase),
            $"Expected model hint to mention Anthropic or Claude but was '{firstModel}'.");
        Assert.Equal(0.50m, byCost[1].CostUsd);
        string secondModel = byCost[1].Model!;
        Assert.True(
            secondModel.Contains("meta", StringComparison.OrdinalIgnoreCase)
                || secondModel.Contains("llama", StringComparison.OrdinalIgnoreCase),
            $"Expected model hint to mention meta or llama but was '{secondModel}'.");
    }

    [Fact]
    public async Task BedrockUsageClient_ThrowsForFailedResponses()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("AccessDenied", Encoding.UTF8, "text/xml"),
            }));

        var options = Options.Create(new BedrockOptions
        {
            AccessKeyId = "AKIA-test",
            SecretAccessKey = "secret",
            Region = "us-east-1",
            CostExplorerRegion = "us-east-1",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1,
        });

        var client = new BedrockUsageClient(httpClient, options, NullLogger<BedrockUsageClient>.Instance);

        var query = new BedrockUsageQuery(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-01-01T01:00:00Z"),
            [],
            3600);

        BedrockUsageClientException exception = await Assert.ThrowsAsync<BedrockUsageClientException>(() =>
            client.GetCloudWatchMetricDataAsync(query, CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.False(exception.IsTransient);
    }

    private static BedrockUsageProvider CreateProvider(
        Action<BedrockOptions> configure,
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var bedrockOptions = new BedrockOptions
        {
            AccessKeyId = "AKIA-test",
            SecretAccessKey = "secret",
            Region = "us-east-1",
            CostExplorerRegion = "us-east-1",
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1,
        };
        configure(bedrockOptions);

        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory));
        var options = Options.Create(bedrockOptions);
        var client = new BedrockUsageClient(httpClient, options, NullLogger<BedrockUsageClient>.Instance);
        return new BedrockUsageProvider(client, options);
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
