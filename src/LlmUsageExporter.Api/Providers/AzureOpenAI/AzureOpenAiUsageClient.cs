// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Security;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.AzureOpenAI;

public sealed class AzureOpenAiUsageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions SerializeOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AzureOpenAiOptions _options;
    private readonly ILogger<AzureOpenAiUsageClient> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedAccessToken;
    private DateTimeOffset _cachedTokenExpiresAt;

    public AzureOpenAiUsageClient(
        HttpClient httpClient,
        IOptions<AzureOpenAiOptions> options,
        ILogger<AzureOpenAiUsageClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AzureMetricsResponse> GetMetricsAsync(
        string accountResourceId,
        AzureOpenAiUsageQuery query,
        CancellationToken cancellationToken)
    {
        string trimmedResource = (accountResourceId ?? string.Empty).Trim().TrimStart('/');
        string managementBase = _options.ManagementBaseUrl.TrimEnd('/');

        List<KeyValuePair<string, string>> parameters =
        [
            new("api-version", "2024-02-01"),
            new("metricnames", "TokenTransaction,ProcessedPromptTokens,GeneratedTokens"),
            new("aggregation", "Total"),
            new("interval", string.IsNullOrWhiteSpace(query.Interval) ? "PT1H" : query.Interval),
            new("timespan", $"{ToIsoUtc(query.Start)}/{ToIsoUtc(query.End)}")
        ];

        string url = $"{managementBase}/{trimmedResource}/providers/Microsoft.Insights/metrics{BuildQueryString(parameters)}";

        return await SendJsonAsync<AzureMetricsResponse>(
            () => new HttpRequestMessage(HttpMethod.Get, url),
            requireAuth: true,
            cancellationToken);
    }

    public async Task<AzureCostQueryResponse> GetCostsAsync(
        AzureOpenAiCostsQuery query,
        CancellationToken cancellationToken)
    {
        string scope = (query.Scope ?? string.Empty).Trim().TrimStart('/');
        string managementBase = _options.ManagementBaseUrl.TrimEnd('/');
        string url = $"{managementBase}/{scope}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";

        var body = new AzureCostQueryRequest
        {
            Type = "ActualCost",
            Timeframe = "Custom",
            TimePeriod = new AzureCostTimePeriod
            {
                From = query.Start.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                To = query.End.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            },
            Dataset = new AzureCostDataset
            {
                Granularity = string.IsNullOrWhiteSpace(query.Granularity) ? "Daily" : query.Granularity,
                Aggregation = new Dictionary<string, AzureCostAggregation>
                {
                    ["totalCost"] = new AzureCostAggregation { Name = "Cost", Function = "Sum" }
                },
                Grouping =
                [
                    new AzureCostGrouping { Type = "Dimension", Name = "ResourceId" }
                ],
                Filter = new AzureCostFilter
                {
                    Dimensions = new AzureCostFilterDimensions
                    {
                        Name = "ServiceName",
                        Operator = "In",
                        Values = ["Cognitive Services"]
                    }
                }
            }
        };

        string payload = JsonSerializer.Serialize(body, SerializeOptions);

        return await SendJsonAsync<AzureCostQueryResponse>(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                return request;
            },
            requireAuth: true,
            cancellationToken);
    }

    internal async Task<string> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTimeOffset.UtcNow < _cachedTokenExpiresAt)
        {
            return _cachedAccessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTimeOffset.UtcNow < _cachedTokenExpiresAt)
            {
                return _cachedAccessToken;
            }

            string loginBase = _options.LoginBaseUrl.TrimEnd('/');
            string tokenUrl = $"{loginBase}/{_options.TenantId}/oauth2/v2.0/token";

            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "client_credentials"),
                new("client_id", _options.ClientId),
                new("client_secret", _options.ClientSecret),
                new("scope", $"{_options.ManagementBaseUrl.TrimEnd('/')}/.default")
            };

            AzureTokenResponse response = await SendJsonAsync<AzureTokenResponse>(
                () => new HttpRequestMessage(HttpMethod.Post, tokenUrl)
                {
                    Content = new FormUrlEncodedContent(form)
                },
                requireAuth: false,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response.AccessToken))
            {
                throw new AzureOpenAiUsageClientException(
                    "Azure AD token response did not include an access_token.",
                    null,
                    isTransient: false);
            }

            long expiresIn = response.ExpiresIn ?? 3600;
            _cachedAccessToken = response.AccessToken;
            _cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn - 60, 30));
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<T> SendJsonAsync<T>(
        Func<HttpRequestMessage> createRequest,
        bool requireAuth,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = createRequest();

            if (requireAuth)
            {
                string token = await EnsureTokenAsync(cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                    T? payload = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
                    return payload ?? throw new AzureOpenAiUsageClientException(
                        "Azure OpenAI API returned an empty response.",
                        response.StatusCode,
                        isTransient: false);
                }

                if (requireAuth && response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _cachedAccessToken = null;
                }

                bool transient = IsTransient(response.StatusCode);
                if (!transient || attempt >= _options.MaxRetries)
                {
                    throw await BuildFailureExceptionAsync(response, transient, cancellationToken);
                }

                _logger.LogWarning(
                    "Azure OpenAI API returned transient status {StatusCode}; retrying attempt {Attempt} of {MaxRetries}",
                    (int)response.StatusCode,
                    attempt + 1,
                    _options.MaxRetries);
            }
            catch (JsonException ex)
            {
                throw new AzureOpenAiUsageClientException("Azure OpenAI API returned malformed JSON.", null, isTransient: false, ex);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Network error calling Azure OpenAI API; retrying attempt {Attempt} of {MaxRetries}", attempt + 1, _options.MaxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Timeout calling Azure OpenAI API; retrying attempt {Attempt} of {MaxRetries}", attempt + 1, _options.MaxRetries);
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken);
        }
    }

    private async Task<AzureOpenAiUsageClientException> BuildFailureExceptionAsync(
        HttpResponseMessage response,
        bool isTransient,
        CancellationToken cancellationToken)
    {
        string body = await SecretRedactor.ReadRedactedBodySnippetAsync(
            response,
            cancellationToken,
            _options.ClientSecret,
            _cachedAccessToken);
        string suffix = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Response body: {body}";

        return new AzureOpenAiUsageClientException(
            $"Azure OpenAI API request failed with status {(int)response.StatusCode} {response.ReasonPhrase}.{suffix}",
            response.StatusCode,
            isTransient);
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        int baseDelay = Math.Max(_options.RetryBaseDelayMilliseconds, 1);
        double delay = baseDelay * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(delay, 30_000));
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return statusCode == HttpStatusCode.TooManyRequests || status >= 500;
    }

    private static string BuildQueryString(IReadOnlyCollection<KeyValuePair<string, string>> parameters)
    {
        if (parameters.Count == 0)
        {
            return string.Empty;
        }

        return "?" + string.Join(
            "&",
            parameters
                .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
                .Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static string ToIsoUtc(DateTimeOffset value)
    {
        return value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}

public sealed class AzureOpenAiUsageClientException : Exception
{
    public AzureOpenAiUsageClientException(
        string message,
        HttpStatusCode? statusCode,
        bool isTransient,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    public HttpStatusCode? StatusCode { get; }

    public bool IsTransient { get; }
}
