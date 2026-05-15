// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using LlmUsageExporter.Api.Configuration;
using LlmUsageExporter.Api.Security;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Bedrock;

public sealed class BedrockUsageClient
{
    private const string CloudWatchService = "monitoring";
    private const string CloudWatchApiVersion = "2010-08-01";
    private const string CostExplorerService = "ce";
    private const string CostExplorerTarget = "AWSInsightsIndexService.GetCostAndUsage";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly BedrockOptions _options;
    private readonly ILogger<BedrockUsageClient> _logger;
    private readonly Func<DateTimeOffset> _clock;

    public BedrockUsageClient(
        HttpClient httpClient,
        IOptions<BedrockOptions> options,
        ILogger<BedrockUsageClient> logger)
        : this(httpClient, options, logger, () => DateTimeOffset.UtcNow)
    {
    }

    internal BedrockUsageClient(
        HttpClient httpClient,
        IOptions<BedrockOptions> options,
        ILogger<BedrockUsageClient> logger,
        Func<DateTimeOffset> clock)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _clock = clock;
    }

    public async Task<IReadOnlyList<BedrockMetricDataResult>> GetCloudWatchMetricDataAsync(
        BedrockUsageQuery query,
        CancellationToken cancellationToken)
    {
        string[] metricNames = ["InputTokenCount", "OutputTokenCount", "Invocations"];
        IReadOnlyCollection<string> modelIds = query.ModelIds.Count == 0
            ? [string.Empty]
            : query.ModelIds;

        List<BedrockMetricDataResult> aggregated = [];
        int identifierIndex = 1;

        foreach (string metric in metricNames)
        {
            foreach (string modelId in modelIds)
            {
                string formBody = BuildCloudWatchFormBody(query, metric, modelId, identifierIndex);
                identifierIndex++;

                string xml = await SendCloudWatchAsync(formBody, cancellationToken);
                IReadOnlyList<BedrockMetricDataResult> parsed = BedrockCloudWatchResponseParser.Parse(xml);
                foreach (BedrockMetricDataResult result in parsed)
                {
                    string label = !string.IsNullOrWhiteSpace(result.Label)
                        ? result.Label
                        : !string.IsNullOrWhiteSpace(modelId) ? modelId : metric;

                    aggregated.Add(result with
                    {
                        Id = $"{metric}|{(!string.IsNullOrEmpty(modelId) ? modelId : "all")}",
                        Label = label,
                    });
                }
            }
        }

        return aggregated;
    }

    public async Task<CostExplorerResponse> GetCostExplorerCostsAsync(
        BedrockCostsQuery query,
        CancellationToken cancellationToken)
    {
        string body = JsonSerializer.Serialize(new
        {
            TimePeriod = new
            {
                Start = query.Start.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                End = query.End.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            },
            Granularity = query.Granularity,
            Metrics = new[] { "UnblendedCost" },
            Filter = new
            {
                Dimensions = new
                {
                    Key = "SERVICE",
                    Values = new[] { "Amazon Bedrock" },
                },
            },
            GroupBy = new[]
            {
                new { Type = "DIMENSION", Key = "USAGE_TYPE" },
            },
        });

        string json = await SendCostExplorerAsync(body, cancellationToken);
        try
        {
            CostExplorerResponse? parsed = JsonSerializer.Deserialize<CostExplorerResponse>(json, JsonOptions);
            return parsed ?? new CostExplorerResponse();
        }
        catch (JsonException ex)
        {
            throw new BedrockUsageClientException("Cost Explorer returned malformed JSON.", null, isTransient: false, ex);
        }
    }

    public AwsCredentials GetCredentials()
    {
        return new AwsCredentials(_options.AccessKeyId, _options.SecretAccessKey, _options.SessionToken);
    }

    private static string BuildCloudWatchFormBody(BedrockUsageQuery query, string metricName, string? modelId, int identifierIndex)
    {
        List<KeyValuePair<string, string>> parameters =
        [
            new("Action", "GetMetricData"),
            new("Version", CloudWatchApiVersion),
            new("StartTime", query.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            new("EndTime", query.End.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)),
            new("MetricDataQueries.member.1.Id", $"m{identifierIndex}"),
            new("MetricDataQueries.member.1.MetricStat.Metric.Namespace", "AWS/Bedrock"),
            new("MetricDataQueries.member.1.MetricStat.Metric.MetricName", metricName),
            new("MetricDataQueries.member.1.MetricStat.Period", query.PeriodSeconds.ToString(CultureInfo.InvariantCulture)),
            new("MetricDataQueries.member.1.MetricStat.Stat", "Sum"),
            new("MetricDataQueries.member.1.ReturnData", "true"),
        ];

        if (!string.IsNullOrWhiteSpace(modelId))
        {
            parameters.Add(new("MetricDataQueries.member.1.MetricStat.Metric.Dimensions.member.1.Name", "ModelId"));
            parameters.Add(new("MetricDataQueries.member.1.MetricStat.Metric.Dimensions.member.1.Value", modelId));
        }

        return string.Join("&", parameters.Select(parameter =>
            Uri.EscapeDataString(parameter.Key) + "=" + Uri.EscapeDataString(parameter.Value)));
    }

    private async Task<string> SendCloudWatchAsync(string formBody, CancellationToken cancellationToken)
    {
        Uri endpoint = ResolveCloudWatchEndpoint();
        byte[] payload = Encoding.UTF8.GetBytes(formBody);

        return await SendSignedAsync(
            endpoint,
            "POST",
            payload,
            "application/x-www-form-urlencoded; charset=utf-8",
            extraHeaders: null,
            service: CloudWatchService,
            region: _options.Region,
            cancellationToken);
    }

    private async Task<string> SendCostExplorerAsync(string jsonBody, CancellationToken cancellationToken)
    {
        Uri endpoint = ResolveCostExplorerEndpoint();
        byte[] payload = Encoding.UTF8.GetBytes(jsonBody);

        Dictionary<string, string> extraHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Amz-Target"] = CostExplorerTarget,
        };

        return await SendSignedAsync(
            endpoint,
            "POST",
            payload,
            "application/x-amz-json-1.1",
            extraHeaders,
            service: CostExplorerService,
            region: _options.CostExplorerRegion,
            cancellationToken);
    }

    private async Task<string> SendSignedAsync(
        Uri endpoint,
        string method,
        byte[] payload,
        string contentType,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string service,
        string region,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            AwsCredentials credentials = GetCredentials();
            DateTimeOffset signingTime = _clock();

            Dictionary<string, string> headersForSigning = new(StringComparer.OrdinalIgnoreCase)
            {
                ["content-type"] = contentType,
            };

            if (extraHeaders is not null)
            {
                foreach (KeyValuePair<string, string> header in extraHeaders)
                {
                    headersForSigning[header.Key] = header.Value;
                }
            }

            AwsSigV4Signature signature = AwsSigV4Signer.Sign(
                method,
                endpoint,
                headersForSigning,
                payload,
                credentials,
                region,
                service,
                signingTime);

            using HttpRequestMessage request = new(new HttpMethod(method), endpoint)
            {
                Content = new ByteArrayContent(payload),
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.Remove("Content-Type");
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);

            if (extraHeaders is not null)
            {
                foreach (KeyValuePair<string, string> header in extraHeaders)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            foreach (KeyValuePair<string, string> header in signature.HeadersToApply)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    return responseBody;
                }

                bool transient = IsTransient(response.StatusCode);
                if (!transient || attempt >= _options.MaxRetries)
                {
                    throw BuildFailureException(response.StatusCode, response.ReasonPhrase, responseBody, transient);
                }

                _logger.LogWarning(
                    "AWS request to {Service} returned transient status {StatusCode}; retrying attempt {Attempt} of {MaxRetries}",
                    service,
                    (int)response.StatusCode,
                    attempt + 1,
                    _options.MaxRetries);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Network error calling AWS {Service}; retrying attempt {Attempt} of {MaxRetries}", service, attempt + 1, _options.MaxRetries);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < _options.MaxRetries)
            {
                _logger.LogWarning(ex, "Timeout calling AWS {Service}; retrying attempt {Attempt} of {MaxRetries}", service, attempt + 1, _options.MaxRetries);
            }

            await Task.Delay(GetRetryDelay(attempt), cancellationToken);
        }
    }

    private Uri ResolveCloudWatchEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_options.CloudWatchEndpointOverride))
        {
            return new Uri(_options.CloudWatchEndpointOverride, UriKind.Absolute);
        }

        return new Uri($"https://monitoring.{_options.Region}.amazonaws.com/", UriKind.Absolute);
    }

    private Uri ResolveCostExplorerEndpoint()
    {
        if (!string.IsNullOrWhiteSpace(_options.CostExplorerEndpointOverride))
        {
            return new Uri(_options.CostExplorerEndpointOverride, UriKind.Absolute);
        }

        return new Uri($"https://ce.{_options.CostExplorerRegion}.amazonaws.com/", UriKind.Absolute);
    }

    private BedrockUsageClientException BuildFailureException(
        HttpStatusCode statusCode,
        string? reasonPhrase,
        string responseBody,
        bool isTransient)
    {
        string redactedBody = SecretRedactor.Redact(
            responseBody.ReplaceLineEndings(" ").Trim(),
            _options.AccessKeyId,
            _options.SecretAccessKey,
            _options.SessionToken);

        string snippet = string.IsNullOrWhiteSpace(redactedBody)
            ? string.Empty
            : " Response body: " + (redactedBody.Length <= 500 ? redactedBody : redactedBody[..500]);

        return new BedrockUsageClientException(
            $"AWS request failed with status {(int)statusCode} {reasonPhrase}.{snippet}",
            statusCode,
            isTransient);
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        int status = (int)statusCode;
        return statusCode == HttpStatusCode.TooManyRequests || status >= 500;
    }

    private TimeSpan GetRetryDelay(int attempt)
    {
        int baseDelay = Math.Max(_options.RetryBaseDelayMilliseconds, 1);
        double delay = baseDelay * Math.Pow(2, attempt);
        return TimeSpan.FromMilliseconds(Math.Min(delay, 30_000));
    }
}

public sealed class BedrockUsageClientException : Exception
{
    public BedrockUsageClientException(
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
