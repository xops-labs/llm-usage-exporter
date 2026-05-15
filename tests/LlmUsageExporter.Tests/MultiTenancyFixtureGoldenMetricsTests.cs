// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Metrics;
using LlmUsageExporter.Api.Providers.Abstractions;
using LlmUsageExporter.Api.Providers.Anthropic;
using LlmUsageExporter.Api.Providers.AzureOpenAI;
using LlmUsageExporter.Api.Providers.Bedrock;
using LlmUsageExporter.Api.Providers.Gemini;
using LlmUsageExporter.Api.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Prometheus;

namespace LlmUsageExporter.Tests;

/// <summary>
/// Replays the <c>*-multi.{json,xml}</c> fixtures through each provider and
/// asserts that the resulting /metrics scrape contains every line in
/// <c>Fixtures/golden-metrics/multi-tenancy-provider-metrics.prom</c>. The
/// multi fixtures cover the cases that the canonical fixture set does not:
/// multiple models, multiple tenancies, and multi-bucket aggregation.
/// </summary>
public sealed class MultiTenancyFixtureGoldenMetricsTests
{
    private const string AzureAccountResourceId =
        "subscriptions/sub-multi/resourceGroups/rg-multi/providers/Microsoft.CognitiveServices/accounts/aoai-multi-a";

    [Fact]
    public async Task MultiScenarioFixtures_MapToGoldenMetricOutput()
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);

        foreach (ProviderScenario scenario in await LoadScenariosAsync())
        {
            var publisher = new LlmMetricsPublisher(scenario.Provider, factory);
            publisher.PublishUsage(scenario.UsageBuckets);
            publisher.PublishCosts(scenario.CostBuckets);
        }

        string metrics = await ScrapeAsync(registry);
        foreach (string expectedLine in LoadGoldenMetricLines("multi-tenancy-provider-metrics.prom"))
        {
            Assert.Contains(expectedLine, metrics);
        }
    }

    [Fact]
    public async Task EmptyProviderResponses_EmitNoUsageOrCostSamples()
    {
        CollectorRegistry registry = Prometheus.Metrics.NewCustomRegistry();
        IMetricFactory factory = Prometheus.Metrics.WithCustomRegistry(registry);

        OpenAiUsageProvider openAi = CreateOpenAiProvider(_ => JsonFixtureResponse("openai-completions-usage-empty.json"));
        IReadOnlyCollection<LlmUsageBucket> usage = await openAi.GetUsageAsync(
            DateTimeOffset.FromUnixTimeSeconds(1720000000),
            DateTimeOffset.FromUnixTimeSeconds(1720003600),
            CancellationToken.None);
        IReadOnlyCollection<LlmCostBucket> costs = await openAi.GetCostsAsync(
            DateTimeOffset.FromUnixTimeSeconds(1720000000),
            DateTimeOffset.FromUnixTimeSeconds(1720086400),
            CancellationToken.None);

        Assert.Empty(usage);
        Assert.Empty(costs);

        var publisher = new LlmMetricsPublisher("openai", factory);
        publisher.PublishUsage(usage);
        publisher.PublishCosts(costs);

        string metrics = await ScrapeAsync(registry);
        foreach (string negativeLine in LoadNegativeMetricLines("empty-provider-metrics.prom"))
        {
            Assert.DoesNotContain(negativeLine, metrics);
        }
    }

    private static async Task<IReadOnlyList<ProviderScenario>> LoadScenariosAsync()
    {
        return
        [
            await LoadOpenAiScenarioAsync(),
            await LoadAzureOpenAiScenarioAsync(),
            await LoadAnthropicScenarioAsync(),
            await LoadGeminiScenarioAsync(),
            await LoadBedrockScenarioAsync()
        ];
    }

    private static async Task<ProviderScenario> LoadOpenAiScenarioAsync()
    {
        OpenAiUsageProvider provider = CreateOpenAiProvider(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path.Contains("/costs", StringComparison.OrdinalIgnoreCase)
                ? JsonFixtureResponse("openai-costs-multi.json")
                : JsonFixtureResponse("openai-completions-usage-multi.json");
        });

        IReadOnlyCollection<LlmUsageBucket> usage = await provider.GetUsageAsync(
            DateTimeOffset.FromUnixTimeSeconds(1720000000),
            DateTimeOffset.FromUnixTimeSeconds(1720007200),
            CancellationToken.None);

        IReadOnlyCollection<LlmCostBucket> costs = await provider.GetCostsAsync(
            DateTimeOffset.FromUnixTimeSeconds(1720000000),
            DateTimeOffset.FromUnixTimeSeconds(1720086400),
            CancellationToken.None);

        return new ProviderScenario("openai", usage, costs);
    }

    private static async Task<ProviderScenario> LoadAzureOpenAiScenarioAsync()
    {
        AzureOpenAiUsageProvider provider = CreateAzureOpenAiProvider(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path.Contains("/oauth2/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{"access_token":"fake-token","token_type":"Bearer","expires_in":3600}""");
            }

            if (path.Contains("Microsoft.CostManagement/query", StringComparison.OrdinalIgnoreCase))
            {
                return JsonFixtureResponse("azure-cost-query-multi.json");
            }

            return JsonFixtureResponse("azure-monitor-metrics-multi.json");
        });

        IReadOnlyCollection<LlmUsageBucket> usage = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-04-09T00:00:00Z"),
            DateTimeOffset.Parse("2024-04-09T01:00:00Z"),
            CancellationToken.None);

        IReadOnlyCollection<LlmCostBucket> costs = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-04-09T00:00:00Z"),
            DateTimeOffset.Parse("2024-04-10T00:00:00Z"),
            CancellationToken.None);

        return new ProviderScenario("azure_openai", usage, costs);
    }

    private static async Task<ProviderScenario> LoadAnthropicScenarioAsync()
    {
        AnthropicUsageProvider provider = CreateAnthropicProvider(request =>
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return path.Contains("cost_report", StringComparison.OrdinalIgnoreCase)
                ? JsonFixtureResponse("anthropic-cost-report-multi.json")
                : JsonFixtureResponse("anthropic-messages-usage-multi.json");
        });

        IReadOnlyCollection<LlmUsageBucket> usage = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-02-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-02-01T02:00:00Z"),
            CancellationToken.None);

        IReadOnlyCollection<LlmCostBucket> costs = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-02-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-02-02T00:00:00Z"),
            CancellationToken.None);

        return new ProviderScenario("anthropic", usage, costs);
    }

    private static async Task<ProviderScenario> LoadGeminiScenarioAsync()
    {
        GeminiUsageProvider provider = CreateGeminiProvider(request =>
        {
            string url = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
            if (url.Contains("/bigquery/v2/", StringComparison.OrdinalIgnoreCase))
            {
                return JsonFixtureResponse("gemini-billing-costs-multi.json");
            }

            if (url.Contains("request_type = \"input\"", StringComparison.Ordinal))
            {
                return JsonFixtureResponse("gemini-input-tokens-multi.json");
            }

            if (url.Contains("request_type = \"output\"", StringComparison.Ordinal))
            {
                return JsonFixtureResponse("gemini-output-tokens-multi.json");
            }

            if (url.Contains("request_count", StringComparison.Ordinal))
            {
                return JsonFixtureResponse("gemini-request-count-multi.json");
            }

            return TextResponse(HttpStatusCode.NotFound, "unknown Gemini fixture route");
        });

        IReadOnlyCollection<LlmUsageBucket> usage = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-02-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-02-01T01:00:00Z"),
            CancellationToken.None);

        IReadOnlyCollection<LlmCostBucket> costs = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-02-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-02-02T00:00:00Z"),
            CancellationToken.None);

        return new ProviderScenario("gemini", usage, costs);
    }

    private static async Task<ProviderScenario> LoadBedrockScenarioAsync()
    {
        BedrockUsageProvider provider = CreateBedrockProvider(request =>
        {
            if (request.Headers.TryGetValues("X-Amz-Target", out IEnumerable<string>? values)
                && values.Any(value => value.Contains("GetCostAndUsage", StringComparison.OrdinalIgnoreCase)))
            {
                return JsonFixtureResponse("bedrock-cost-explorer-multi.json", "application/x-amz-json-1.1");
            }

            string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            if (body.Contains("InputTokenCount", StringComparison.Ordinal))
            {
                return XmlFixtureResponse("bedrock-cloudwatch-input-multi.xml");
            }

            if (body.Contains("OutputTokenCount", StringComparison.Ordinal))
            {
                return XmlFixtureResponse("bedrock-cloudwatch-output-multi.xml");
            }

            if (body.Contains("Invocations", StringComparison.Ordinal))
            {
                return XmlFixtureResponse("bedrock-cloudwatch-invocations-multi.xml");
            }

            return TextResponse(HttpStatusCode.NotFound, "unknown Bedrock fixture route");
        });

        IReadOnlyCollection<LlmUsageBucket> usage = await provider.GetUsageAsync(
            DateTimeOffset.Parse("2024-02-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-02-01T02:00:00Z"),
            CancellationToken.None);

        IReadOnlyCollection<LlmCostBucket> costs = await provider.GetCostsAsync(
            DateTimeOffset.Parse("2024-02-01T00:00:00Z"),
            DateTimeOffset.Parse("2024-02-02T00:00:00Z"),
            CancellationToken.None);

        return new ProviderScenario("bedrock", usage, costs);
    }

    private static OpenAiUsageProvider CreateOpenAiProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
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

    private static AzureOpenAiUsageProvider CreateAzureOpenAiProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory));
        var options = Options.Create(new AzureOpenAiOptions
        {
            TenantId = "tenant-multi",
            ClientId = "client-multi",
            ClientSecret = "secret-multi",
            SubscriptionId = "sub-multi",
            ResourceGroup = "rg-multi",
            AccountResourceIds = [AzureAccountResourceId],
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        });

        var client = new AzureOpenAiUsageClient(httpClient, options, NullLogger<AzureOpenAiUsageClient>.Instance);
        return new AzureOpenAiUsageProvider(client, options);
    }

    private static AnthropicUsageProvider CreateAnthropicProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
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

    private static GeminiUsageProvider CreateGeminiProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory))
        {
            BaseAddress = new Uri("https://monitoring.googleapis.com")
        };

        IOptionsMonitor<GeminiOptions> monitor = new StaticOptionsMonitor<GeminiOptions>(new GeminiOptions
        {
            ProjectId = "gemini-multi-proj",
            BillingProjectId = "billing-query-proj",
            BillingDatasetProject = "billing-proj",
            BillingDatasetId = "billing",
            BillingTable = "gcp_billing_export",
            AccessToken = "test-token",
            EnableCostQueries = true,
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

    private static BedrockUsageProvider CreateBedrockProvider(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        var bedrockOptions = new BedrockOptions
        {
            AccessKeyId = "AKIA-multi",
            SecretAccessKey = "secret-multi",
            Region = "us-east-1",
            CostExplorerRegion = "us-east-1",
            ModelIds = ["anthropic.claude-3-5-sonnet-multi-v1:0"],
            MetricsPeriodSeconds = 3600,
            EnableCostQueries = true,
            MaxRetries = 0,
            RetryBaseDelayMilliseconds = 1
        };

        var httpClient = new HttpClient(new StubHttpMessageHandler(responseFactory));
        var options = Options.Create(bedrockOptions);
        var client = new BedrockUsageClient(httpClient, options, NullLogger<BedrockUsageClient>.Instance);
        return new BedrockUsageProvider(client, options);
    }

    private static IReadOnlyList<string> LoadGoldenMetricLines(string fileName)
    {
        return File.ReadAllLines(FixturePath("golden-metrics", fileName), Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("NEGATIVE:", StringComparison.Ordinal))
            .ToArray();
    }

    private static IReadOnlyList<string> LoadNegativeMetricLines(string fileName)
    {
        return File.ReadAllLines(FixturePath("golden-metrics", fileName), Encoding.UTF8)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("NEGATIVE:", StringComparison.Ordinal))
            .Select(line => line["NEGATIVE:".Length..])
            .ToArray();
    }

    private static HttpResponseMessage JsonFixtureResponse(string fileName, string mediaType = "application/json")
    {
        return JsonResponse(LoadFixture("provider-payloads", fileName), mediaType);
    }

    private static HttpResponseMessage XmlFixtureResponse(string fileName)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(LoadFixture("provider-payloads", fileName), Encoding.UTF8, "text/xml")
        };
    }

    private static HttpResponseMessage JsonResponse(string json, string mediaType = "application/json")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, mediaType)
        };
    }

    private static HttpResponseMessage TextResponse(HttpStatusCode statusCode, string text)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain")
        };
    }

    private static string LoadFixture(string group, string fileName)
    {
        return File.ReadAllText(FixturePath(group, fileName), Encoding.UTF8);
    }

    private static string FixturePath(string group, string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", group, fileName);
    }

    private static async Task<string> ScrapeAsync(CollectorRegistry registry)
    {
        await using var stream = new MemoryStream();
        await registry.CollectAndExportAsTextAsync(stream, CancellationToken.None);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed record ProviderScenario(
        string Provider,
        IReadOnlyCollection<LlmUsageBucket> UsageBuckets,
        IReadOnlyCollection<LlmCostBucket> CostBuckets);

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
