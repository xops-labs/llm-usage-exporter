// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Security;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Anthropic;

public sealed class AnthropicUsageClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AnthropicOptions _options;
    private readonly ILogger<AnthropicUsageClient> _logger;

    public AnthropicUsageClient(
        HttpClient httpClient,
        IOptions<AnthropicOptions> options,
        ILogger<AnthropicUsageClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        }
    }

    public Task<IReadOnlyList<AnthropicUsageBucketDto>> GetMessagesUsageAsync(
        AnthropicUsageQuery query,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> parameters =
        [
            new("starting_at", ToIso8601(query.Start)),
            new("ending_at", ToIso8601(query.End)),
            new("bucket_width", query.BucketWidth),
            new("limit", query.Limit.ToString(CultureInfo.InvariantCulture))
        ];

        AddRepeated(parameters, "group_by[]", query.GroupBy);
        AddRepeated(parameters, "workspace_ids[]", query.WorkspaceIds);
        AddRepeated(parameters, "models[]", query.Models);
        AddRepeated(parameters, "api_key_ids[]", query.ApiKeyIds);

        return GetPaginatedAsync<AnthropicUsageResponse, AnthropicUsageBucketDto>(
            "/v1/organizations/usage_report/messages",
            parameters,
            response => response.Data ?? [],
            response => response.NextPage,
            cancellationToken);
    }

    public Task<IReadOnlyList<AnthropicCostBucketDto>> GetCostsAsync(
        AnthropicCostsQuery query,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> parameters =
        [
            new("starting_at", ToIso8601(query.Start)),
            new("ending_at", ToIso8601(query.End)),
            new("bucket_width", query.BucketWidth),
            new("limit", query.Limit.ToString(CultureInfo.InvariantCulture))
        ];

        AddRepeated(parameters, "group_by[]", query.GroupBy);
        AddRepeated(parameters, "workspace_ids[]", query.WorkspaceIds);

        return GetPaginatedAsync<AnthropicCostResponse, AnthropicCostBucketDto>(
            "/v1/organizations/cost_report",
            parameters,
            response => response.Data ?? [],
            response => response.NextPage,
            cancellationToken);
    }

    private async Task<IReadOnlyList<TBucket>> GetPaginatedAsync<TResponse, TBucket>(
        string path,
        IReadOnlyCollection<KeyValuePair<string, string>> baseParameters,
        Func<TResponse, IReadOnlyCollection<TBucket>> selectData,
        Func<TResponse, string?> selectNextPage,
        CancellationToken cancellationToken)
    {
        List<TBucket> results = [];
        HashSet<string> seenPages = new(StringComparer.Ordinal);
        string? page = null;

        do
        {
            List<KeyValuePair<string, string>> parameters = [.. baseParameters];
            if (!string.IsNullOrWhiteSpace(page))
            {
                if (!seenPages.Add(page))
                {
                    _logger.LogWarning("Anthropic pagination returned repeated cursor; stopping pagination");
                    break;
                }

                parameters.Add(new KeyValuePair<string, string>("page", page));
            }

            string url = path + BuildQueryString(parameters);
            TResponse response = await SendJsonAsync<TResponse>(() => CreateRequest(url), cancellationToken);

            results.AddRange(selectData(response));
            page = selectNextPage(response);
        }
        while (!string.IsNullOrWhiteSpace(page));

        return results;
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("x-api-key", _options.AdminApiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", _options.AnthropicVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return request;
    }

    private async Task<T> SendJsonAsync<T>(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = createRequest();

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
                    return payload ?? throw new AnthropicUsageClientException(
                        "Anthropic usage API returned an empty response.",
                        response.StatusCode,
                        isTransient: false);
                }

                bool transient = IsTransient(response.StatusCode);
                if (!transient || attempt >= _options.MaxRetries)
                {
                    throw await BuildFailureExceptionAsync(response, transient, cancellationToken);
                }

                _logger.LogWarning(
                    "Anthropic usage API returned transient status {StatusCode}; retrying attempt {Attempt} of {MaxRetries}",
                    (int)response.StatusCode,
                    attempt + 1,
                    _options.MaxRetries);
            }
            catch (JsonException ex)
            {
                throw new AnthropicUsageClientException("Anthropic usage API returned malformed JSON.", null, isTransient: false, ex);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Network error calling Anthropic usage API; retrying attempt {Attempt} of {MaxRetries}", attempt + 1, _options.MaxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Timeout calling Anthropic usage API; retrying attempt {Attempt} of {MaxRetries}", attempt + 1, _options.MaxRetries);
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken);
        }
    }

    private async Task<AnthropicUsageClientException> BuildFailureExceptionAsync(
        HttpResponseMessage response,
        bool isTransient,
        CancellationToken cancellationToken)
    {
        string body = await SecretRedactor.ReadRedactedBodySnippetAsync(response, cancellationToken, _options.AdminApiKey);
        string suffix = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Response body: {body}";

        return new AnthropicUsageClientException(
            $"Anthropic usage API request failed with status {(int)response.StatusCode} {response.ReasonPhrase}.{suffix}",
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

    private static void AddRepeated(
        ICollection<KeyValuePair<string, string>> parameters,
        string name,
        IEnumerable<string> values)
    {
        foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            parameters.Add(new KeyValuePair<string, string>(name, value.Trim()));
        }
    }

    private static string ToIso8601(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}

public sealed class AnthropicUsageClientException : Exception
{
    public AnthropicUsageClientException(
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
