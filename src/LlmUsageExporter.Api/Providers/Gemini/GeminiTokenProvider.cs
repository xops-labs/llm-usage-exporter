// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmUsageExporter.Api.Configuration;
using Microsoft.Extensions.Options;

namespace LlmUsageExporter.Api.Providers.Gemini;

public sealed class GeminiTokenProvider
{
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private const string GrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IOptionsMonitor<GeminiOptions> _options;
    private readonly ILogger<GeminiTokenProvider> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedExpiresAt = DateTimeOffset.MinValue;

    public GeminiTokenProvider(
        HttpClient httpClient,
        IOptionsMonitor<GeminiOptions> options,
        ILogger<GeminiTokenProvider> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        GeminiOptions options = _options.CurrentValue;

        if (!string.IsNullOrWhiteSpace(options.AccessToken))
        {
            return options.AccessToken.Trim();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_cachedToken is not null && now < _cachedExpiresAt)
        {
            return _cachedToken;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedToken is not null && now < _cachedExpiresAt)
            {
                return _cachedToken;
            }

            if (string.IsNullOrWhiteSpace(options.ServiceAccountKeyFile))
            {
                throw new InvalidOperationException(
                    "Gemini provider requires either Gemini:AccessToken or Gemini:ServiceAccountKeyFile to be configured.");
            }

            ServiceAccountKey key = LoadServiceAccountKey(options.ServiceAccountKeyFile);
            string assertion = BuildJwtAssertion(key, options);
            (string token, int expiresIn) = await ExchangeAssertionAsync(options, assertion, cancellationToken);

            _cachedToken = token;
            _cachedExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn - 60, 60));
            return token;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static ServiceAccountKey LoadServiceAccountKey(string path)
    {
        string contents = File.ReadAllText(path);
        ServiceAccountKey? key = JsonSerializer.Deserialize<ServiceAccountKey>(contents, JsonOptions);
        if (key is null || string.IsNullOrWhiteSpace(key.ClientEmail) || string.IsNullOrWhiteSpace(key.PrivateKey))
        {
            throw new InvalidOperationException($"Service account key file '{path}' is missing required fields.");
        }

        return key;
    }

    private static string BuildJwtAssertion(ServiceAccountKey key, GeminiOptions options)
    {
        long iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long exp = iat + 3600;
        string audience = options.OAuthBaseUrl.TrimEnd('/') + "/token";

        var header = new Dictionary<string, object>
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT"
        };

        var payload = new Dictionary<string, object>
        {
            ["iss"] = key.ClientEmail!,
            ["scope"] = Scope,
            ["aud"] = audience,
            ["iat"] = iat,
            ["exp"] = exp
        };

        string encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        string encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        string signingInput = $"{encodedHeader}.{encodedPayload}";

        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKey);
        byte[] signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private async Task<(string Token, int ExpiresIn)> ExchangeAssertionAsync(
        GeminiOptions options,
        string assertion,
        CancellationToken cancellationToken)
    {
        string url = options.OAuthBaseUrl.TrimEnd('/') + "/token";

        var formContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", GrantType),
            new KeyValuePair<string, string>("assertion", assertion)
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = formContent };
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Failed to exchange Gemini service-account JWT for an access token; status {StatusCode}",
                (int)response.StatusCode);
            throw new InvalidOperationException(
                $"Gemini OAuth2 token exchange failed with status {(int)response.StatusCode}.");
        }

        OAuthTokenResponse? token = JsonSerializer.Deserialize<OAuthTokenResponse>(body, JsonOptions);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Gemini OAuth2 token exchange returned an empty access token.");
        }

        int expiresIn = token.ExpiresIn ?? 3600;
        return (token.AccessToken!, expiresIn);
    }

    private static string Base64UrlEncode(byte[] data)
    {
        string base64 = Convert.ToBase64String(data);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private sealed class ServiceAccountKey
    {
        [JsonPropertyName("client_email")]
        public string? ClientEmail { get; init; }

        [JsonPropertyName("private_key")]
        public string? PrivateKey { get; init; }

        [JsonPropertyName("token_uri")]
        public string? TokenUri { get; init; }
    }

    private sealed class OAuthTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; init; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; init; }
    }
}
