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

namespace LlmUsageExporter.Api.Providers.Gemini;

public sealed class GeminiUsageClient
{
    private const string InputTokensFilter =
        "metric.type = \"aiplatform.googleapis.com/publisher/online_serving/token_count\" AND metric.labels.request_type = \"input\"";

    private const string OutputTokensFilter =
        "metric.type = \"aiplatform.googleapis.com/publisher/online_serving/token_count\" AND metric.labels.request_type = \"output\"";

    private const string RequestCountFilter =
        "metric.type = \"aiplatform.googleapis.com/publisher/online_serving/request_count\"";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<GeminiOptions> _options;
    private readonly GeminiTokenProvider _tokenProvider;
    private readonly ILogger<GeminiUsageClient> _logger;

    public GeminiUsageClient(
        HttpClient httpClient,
        IOptionsMonitor<GeminiOptions> options,
        GeminiTokenProvider tokenProvider,
        ILogger<GeminiUsageClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public Task<IReadOnlyList<GeminiTimeSeries>> GetInputTokenSeriesAsync(
        GeminiUsageQuery query,
        CancellationToken cancellationToken)
    {
        return GetTimeSeriesAsync(InputTokensFilter, query, cancellationToken);
    }

    public Task<IReadOnlyList<GeminiTimeSeries>> GetOutputTokenSeriesAsync(
        GeminiUsageQuery query,
        CancellationToken cancellationToken)
    {
        return GetTimeSeriesAsync(OutputTokensFilter, query, cancellationToken);
    }

    public Task<IReadOnlyList<GeminiTimeSeries>> GetRequestCountSeriesAsync(
        GeminiUsageQuery query,
        CancellationToken cancellationToken)
    {
        return GetTimeSeriesAsync(RequestCountFilter, query, cancellationToken);
    }

    public async Task<GeminiBigQueryResponse> GetBillingCostsAsync(
        GeminiCostsQuery query,
        CancellationToken cancellationToken)
    {
        GeminiOptions options = _options.CurrentValue;

        if (string.IsNullOrWhiteSpace(options.BillingDatasetProject)
            || string.IsNullOrWhiteSpace(options.BillingDatasetId)
            || string.IsNullOrWhiteSpace(options.BillingTable))
        {
            throw new GeminiUsageClientException(
                "Gemini cost queries require BillingDatasetProject, BillingDatasetId, and BillingTable to be configured.",
                statusCode: null,
                isTransient: false);
        }

        string billingProjectId = !string.IsNullOrWhiteSpace(options.BillingProjectId)
            ? options.BillingProjectId!
            : options.ProjectId;

        string url = options.BigQueryBaseUrl.TrimEnd('/')
            + $"/bigquery/v2/projects/{Uri.EscapeDataString(billingProjectId)}/queries";

        string sql =
            "SELECT TIMESTAMP_TRUNC(usage_start_time, DAY) AS day, sku.description AS sku, SUM(cost) AS cost, currency "
            + $"FROM `{options.BillingDatasetProject}.{options.BillingDatasetId}.{options.BillingTable}` "
            + "WHERE service.description = 'Vertex AI' AND usage_start_time >= @start AND usage_start_time < @end "
            + "GROUP BY day, sku, currency ORDER BY day";

        var payload = new
        {
            query = sql,
            useLegacySql = false,
            queryParameters = new object[]
            {
                new
                {
                    name = "start",
                    parameterType = new { type = "TIMESTAMP" },
                    parameterValue = new { value = query.Start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) }
                },
                new
                {
                    name = "end",
                    parameterType = new { type = "TIMESTAMP" },
                    parameterValue = new { value = query.End.UtcDateTime.ToString("o", CultureInfo.InvariantCulture) }
                }
            }
        };

        byte[] bodyBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);

        GeminiBigQueryResponse response = await SendJsonAsync<GeminiBigQueryResponse>(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new ByteArrayContent(bodyBytes)
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return request;
            },
            cancellationToken);

        return response;
    }

    private async Task<IReadOnlyList<GeminiTimeSeries>> GetTimeSeriesAsync(
        string filter,
        GeminiUsageQuery query,
        CancellationToken cancellationToken)
    {
        GeminiOptions options = _options.CurrentValue;
        List<GeminiTimeSeries> series = [];
        HashSet<string> seenPages = new(StringComparer.Ordinal);
        string? pageToken = null;

        do
        {
            string url = BuildMonitoringUrl(options, filter, query, pageToken);
            GeminiTimeSeriesResponse response = await SendJsonAsync<GeminiTimeSeriesResponse>(
                () => new HttpRequestMessage(HttpMethod.Get, url),
                cancellationToken);

            if (response.TimeSeries is { Count: > 0 })
            {
                series.AddRange(response.TimeSeries);
            }

            pageToken = response.NextPageToken;
            if (!string.IsNullOrWhiteSpace(pageToken) && !seenPages.Add(pageToken))
            {
                _logger.LogWarning("Gemini Cloud Monitoring pagination returned repeated cursor; stopping pagination");
                break;
            }
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return series;
    }

    private static string BuildMonitoringUrl(
        GeminiOptions options,
        string filter,
        GeminiUsageQuery query,
        string? pageToken)
    {
        string baseUrl = options.MonitoringBaseUrl.TrimEnd('/')
            + $"/v3/projects/{Uri.EscapeDataString(options.ProjectId)}/timeSeries";

        List<KeyValuePair<string, string>> parameters =
        [
            new("filter", filter),
            new("interval.startTime", query.Start.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)),
            new("interval.endTime", query.End.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)),
            new("aggregation.alignmentPeriod", query.AlignmentPeriod),
            new("aggregation.perSeriesAligner", "ALIGN_SUM"),
            new("aggregation.crossSeriesReducer", "REDUCE_SUM"),
            new("aggregation.groupByFields", "metric.labels.model_id"),
            new("aggregation.groupByFields", "resource.labels.project_id"),
            new("pageSize", "1000")
        ];

        if (!string.IsNullOrWhiteSpace(pageToken))
        {
            parameters.Add(new KeyValuePair<string, string>("pageToken", pageToken));
        }

        StringBuilder builder = new();
        builder.Append(baseUrl);
        builder.Append('?');
        bool first = true;
        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Value))
            {
                continue;
            }

            if (!first)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(parameter.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(parameter.Value));
            first = false;
        }

        return builder.ToString();
    }

    private async Task<T> SendJsonAsync<T>(
        Func<HttpRequestMessage> createRequest,
        CancellationToken cancellationToken)
    {
        GeminiOptions options = _options.CurrentValue;
        string accessToken = await _tokenProvider.GetAccessTokenAsync(cancellationToken);

        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
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
                    return payload ?? throw new GeminiUsageClientException(
                        "Gemini API returned an empty response.",
                        response.StatusCode,
                        isTransient: false);
                }

                bool transient = IsTransient(response.StatusCode);
                if (!transient || attempt >= options.MaxRetries)
                {
                    throw await BuildFailureExceptionAsync(response, transient, cancellationToken);
                }

                _logger.LogWarning(
                    "Gemini API returned transient status {StatusCode}; retrying attempt {Attempt} of {MaxRetries}",
                    (int)response.StatusCode,
                    attempt + 1,
                    options.MaxRetries);
            }
            catch (JsonException ex)
            {
                throw new GeminiUsageClientException("Gemini API returned malformed JSON.", null, isTransient: false, ex);
            }
            catch (HttpRequestException ex) when (attempt < options.MaxRetries)
            {
                _logger.LogWarning(ex, "Network error calling Gemini API; retrying attempt {Attempt} of {MaxRetries}", attempt + 1, options.MaxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < options.MaxRetries)
            {
                _logger.LogWarning(ex, "Timeout calling Gemini API; retrying attempt {Attempt} of {MaxRetries}", attempt + 1, options.MaxRetries);
            }

            await Task.Delay(GetRetryDelay(attempt, options), cancellationToken);
        }
    }

    private async Task<GeminiUsageClientException> BuildFailureExceptionAsync(
        HttpResponseMessage response,
        bool isTransient,
        CancellationToken cancellationToken)
    {
        GeminiOptions options = _options.CurrentValue;
        string body = await SecretRedactor.ReadRedactedBodySnippetAsync(
            response,
            cancellationToken,
            options.AccessToken);
        string suffix = string.IsNullOrWhiteSpace(body) ? string.Empty : $" Response body: {body}";

        return new GeminiUsageClientException(
            $"Gemini API request failed with status {(int)response.StatusCode} {response.ReasonPhrase}.{suffix}",
            response.StatusCode,
            isTransient);
    }

    private static TimeSpan GetRetryDelay(int attempt, GeminiOptions options)
    {
        int baseDelay = Math.Max(options.RetryBaseDelayMilliseconds, 1);
        double delay = baseDelay * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(delay, 30_000));
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return statusCode == HttpStatusCode.TooManyRequests || status >= 500;
    }
}

public sealed class GeminiUsageClientException : Exception
{
    public GeminiUsageClientException(
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
