// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LlmUsageExporter.Api.Providers.Bedrock;

public static class AwsSigV4Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string AmzDateFormat = "yyyyMMddTHHmmssZ";
    private const string DateFormat = "yyyyMMdd";

    public static AwsSigV4Signature Sign(
        string method,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        ReadOnlySpan<byte> body,
        AwsCredentials credentials,
        string region,
        string service,
        DateTimeOffset signingTime)
    {
        string amzDate = signingTime.UtcDateTime.ToString(AmzDateFormat, CultureInfo.InvariantCulture);
        string dateStamp = signingTime.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture);

        SortedDictionary<string, string> normalizedHeaders = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> header in headers)
        {
            normalizedHeaders[header.Key.ToLowerInvariant()] = CollapseWhitespace(header.Value);
        }

        normalizedHeaders["host"] = uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        normalizedHeaders["x-amz-date"] = amzDate;

        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            normalizedHeaders["x-amz-security-token"] = credentials.SessionToken;
        }

        string payloadHash = HexHash(body);
        normalizedHeaders["x-amz-content-sha256"] = payloadHash;

        StringBuilder canonicalHeadersBuilder = new();
        StringBuilder signedHeadersBuilder = new();
        bool first = true;
        foreach (KeyValuePair<string, string> header in normalizedHeaders)
        {
            canonicalHeadersBuilder.Append(header.Key).Append(':').Append(header.Value).Append('\n');
            if (!first)
            {
                signedHeadersBuilder.Append(';');
            }
            signedHeadersBuilder.Append(header.Key);
            first = false;
        }

        string signedHeaders = signedHeadersBuilder.ToString();
        string canonicalUri = CanonicalizePath(uri.AbsolutePath);
        string canonicalQuery = CanonicalizeQuery(uri.Query);

        string canonicalRequest = string.Join(
            "\n",
            method.ToUpperInvariant(),
            canonicalUri,
            canonicalQuery,
            canonicalHeadersBuilder.ToString(),
            signedHeaders,
            payloadHash);

        string credentialScope = string.Join("/", dateStamp, region, service, "aws4_request");
        string stringToSign = string.Join(
            "\n",
            Algorithm,
            amzDate,
            credentialScope,
            HexHash(Encoding.UTF8.GetBytes(canonicalRequest)));

        byte[] kSecret = Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey);
        byte[] kDate = HmacSha256(kSecret, dateStamp);
        byte[] kRegion = HmacSha256(kDate, region);
        byte[] kService = HmacSha256(kRegion, service);
        byte[] kSigning = HmacSha256(kService, "aws4_request");
        string signature = ToHex(HmacSha256(kSigning, stringToSign));

        string authorization =
            $"{Algorithm} Credential={credentials.AccessKeyId}/{credentialScope}, " +
            $"SignedHeaders={signedHeaders}, Signature={signature}";

        Dictionary<string, string> headersToApply = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = authorization,
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Content-Sha256"] = payloadHash,
        };

        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            headersToApply["X-Amz-Security-Token"] = credentials.SessionToken;
        }

        return new AwsSigV4Signature(
            authorization,
            amzDate,
            signedHeaders,
            canonicalRequest,
            stringToSign,
            signature,
            credentialScope,
            headersToApply);
    }

    private static string CanonicalizePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        string[] segments = path.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            segments[index] = UriEncode(segments[index], encodeSlash: false);
        }
        return string.Join("/", segments);
    }

    private static string CanonicalizeQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        string trimmed = query.StartsWith('?') ? query[1..] : query;
        string[] parts = trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries);
        List<KeyValuePair<string, string>> encoded = new(parts.Length);

        foreach (string part in parts)
        {
            int equalsIndex = part.IndexOf('=');
            string rawKey = equalsIndex < 0 ? part : part[..equalsIndex];
            string rawValue = equalsIndex < 0 ? string.Empty : part[(equalsIndex + 1)..];
            encoded.Add(new KeyValuePair<string, string>(
                UriEncode(Uri.UnescapeDataString(rawKey), encodeSlash: true),
                UriEncode(Uri.UnescapeDataString(rawValue), encodeSlash: true)));
        }

        encoded.Sort((left, right) =>
        {
            int keyCompare = string.CompareOrdinal(left.Key, right.Key);
            return keyCompare != 0 ? keyCompare : string.CompareOrdinal(left.Value, right.Value);
        });

        return string.Join("&", encoded.Select(pair => pair.Key + "=" + pair.Value));
    }

    private static string UriEncode(string value, bool encodeSlash)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if ((character >= 'A' && character <= 'Z')
                || (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character == '-'
                || character == '_'
                || character == '.'
                || character == '~')
            {
                builder.Append(character);
            }
            else if (character == '/' && !encodeSlash)
            {
                builder.Append(character);
            }
            else
            {
                byte[] bytes = Encoding.UTF8.GetBytes(new[] { character });
                foreach (byte b in bytes)
                {
                    builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
                }
            }
        }
        return builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        StringBuilder builder = new(trimmed.Length);
        bool previousWasSpace = false;
        foreach (char character in trimmed)
        {
            bool isSpace = character == ' ';
            if (isSpace && previousWasSpace)
            {
                continue;
            }
            builder.Append(character);
            previousWasSpace = isSpace;
        }
        return builder.ToString();
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using HMACSHA256 hmac = new(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }

    private static string HexHash(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(body, hash);
        return ToHex(hash);
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        StringBuilder builder = new(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            builder.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }
}

public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null);

public sealed record AwsSigV4Signature(
    string Authorization,
    string AmzDate,
    string SignedHeaders,
    string CanonicalRequest,
    string StringToSign,
    string Signature,
    string CredentialScope,
    IReadOnlyDictionary<string, string> HeadersToApply);
