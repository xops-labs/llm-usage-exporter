// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;

namespace LlmUsageExporter.Api.Security;

public static class SecretRedactor
{
    private const string Redacted = "[redacted]";

    private static readonly string[] SensitiveFieldNames =
    [
        "authorization",
        "x-api-key",
        "api-key",
        "api_key",
        "openai_admin_api_key",
        "anthropic_admin_api_key",
        "admin_api_key",
        "adminApiKey",
        "access_token",
        "accessToken",
        "gemini_access_token",
        "client_secret",
        "clientSecret",
        "azure_openai_client_secret",
        "id_token",
        "refresh_token",
        "assertion",
        "private_key",
        "privateKey",
        "gemini_service_account_key_json",
        "access_key_id",
        "accessKeyId",
        "aws_access_key_id",
        "secret_access_key",
        "secretAccessKey",
        "aws_secret_access_key",
        "session_token",
        "sessionToken",
        "aws_session_token",
        "x-amz-security-token",
        "otel_exporter_otlp_headers"
    ];

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)\bBearer\s+[^""'\s,;]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Redact(string? value, params string?[] knownSecrets)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string result = value;

        foreach (string secret in knownSecrets
            .Where(secret => !string.IsNullOrWhiteSpace(secret) && secret.Trim().Length >= 4)
            .Select(secret => secret!.Trim())
            .Distinct(StringComparer.Ordinal))
        {
            result = result.Replace(secret, Redacted, StringComparison.Ordinal);
        }

        result = BearerTokenRegex.Replace(result, "Bearer " + Redacted);

        foreach (string fieldName in SensitiveFieldNames)
        {
            string escaped = Regex.Escape(fieldName);
            result = Regex.Replace(
                result,
                $"(?i)(\"{escaped}\"\\s*:\\s*\")([^\"]*)(\")",
                "$1" + Redacted + "$3",
                RegexOptions.CultureInvariant);
            result = Regex.Replace(
                result,
                $"(?i)(\\b{escaped}\\b\\s*[:=]\\s*)([^\\s,;&]+)",
                "$1" + Redacted,
                RegexOptions.CultureInvariant);
        }

        return result;
    }

    public static async Task<string> ReadRedactedBodySnippetAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        params string?[] knownSecrets)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        string redacted = Redact(body.ReplaceLineEndings(" ").Trim(), knownSecrets);
        return redacted.Length <= 500 ? redacted : redacted[..500];
    }
}
