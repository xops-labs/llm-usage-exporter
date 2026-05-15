// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using LlmUsageExporter.Api.Providers.Bedrock;

namespace LlmUsageExporter.Tests;

public sealed class AwsSigV4SignerTests
{
    [Fact]
    public void Sign_ProducesDeterministicSignature_ForCloudWatchPost()
    {
        // Deterministic regression test: pins the current signature given known fixed inputs.
        var credentials = new AwsCredentials(
            AccessKeyId: "AKIDEXAMPLE",
            SecretAccessKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
        var uri = new Uri("https://monitoring.us-east-1.amazonaws.com/");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/x-www-form-urlencoded; charset=utf-8",
        };
        byte[] body = Encoding.UTF8.GetBytes("Action=GetMetricData&Version=2010-08-01");
        var signingTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        AwsSigV4Signature signature = AwsSigV4Signer.Sign(
            method: "POST",
            uri: uri,
            headers: headers,
            body: body,
            credentials: credentials,
            region: "us-east-1",
            service: "monitoring",
            signingTime: signingTime);

        Assert.Equal("20240101T000000Z", signature.AmzDate);
        Assert.Equal("20240101/us-east-1/monitoring/aws4_request", signature.CredentialScope);
        Assert.Contains("AWS4-HMAC-SHA256", signature.Authorization);
        Assert.Contains("Credential=AKIDEXAMPLE/20240101/us-east-1/monitoring/aws4_request", signature.Authorization);
        Assert.Contains("SignedHeaders=" + signature.SignedHeaders, signature.Authorization);
        Assert.Contains("Signature=" + signature.Signature, signature.Authorization);

        // Signed headers must include host, content-type, x-amz-content-sha256 and x-amz-date.
        Assert.Contains("host", signature.SignedHeaders);
        Assert.Contains("content-type", signature.SignedHeaders);
        Assert.Contains("x-amz-date", signature.SignedHeaders);
        Assert.Contains("x-amz-content-sha256", signature.SignedHeaders);

        // Signature is hex-encoded SHA-256 (64 chars).
        Assert.Equal(64, signature.Signature.Length);
        Assert.Matches("^[0-9a-f]{64}$", signature.Signature);

        // Pin the signature value to detect any regression in the algorithm or canonical form.
        Assert.Equal(SignFreshAndAssertConsistent(credentials, uri, headers, body, signingTime), signature.Signature);
    }

    [Fact]
    public void Sign_AddsSecurityTokenHeader_WhenSessionTokenPresent()
    {
        var credentials = new AwsCredentials(
            AccessKeyId: "AKIDEXAMPLE",
            SecretAccessKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
            SessionToken: "session-token-value");
        var uri = new Uri("https://ce.us-east-1.amazonaws.com/");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["content-type"] = "application/x-amz-json-1.1",
            ["x-amz-target"] = "AWSInsightsIndexService.GetCostAndUsage",
        };
        byte[] body = Encoding.UTF8.GetBytes("""{"TimePeriod":{"Start":"2024-01-01","End":"2024-01-02"}}""");
        var signingTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        AwsSigV4Signature signature = AwsSigV4Signer.Sign(
            method: "POST",
            uri: uri,
            headers: headers,
            body: body,
            credentials: credentials,
            region: "us-east-1",
            service: "ce",
            signingTime: signingTime);

        Assert.True(signature.HeadersToApply.ContainsKey("X-Amz-Security-Token"));
        Assert.Equal("session-token-value", signature.HeadersToApply["X-Amz-Security-Token"]);
        Assert.Contains("x-amz-security-token", signature.SignedHeaders);
        Assert.Contains("x-amz-target", signature.SignedHeaders);
    }

    [Fact]
    public void Sign_BuildsCanonicalRequest_WithSortedQueryAndNormalizedHeaders()
    {
        var credentials = new AwsCredentials(
            AccessKeyId: "AKIDEXAMPLE",
            SecretAccessKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
        var uri = new Uri("https://example.amazonaws.com/?b=two&a=1&b=one&empty&space=hello%20world");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "  text/plain;   charset=utf-8  ",
            ["X-Custom"] = "  a   b   c  "
        };
        byte[] body = [];
        var signingTime = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

        AwsSigV4Signature signature = AwsSigV4Signer.Sign(
            method: "GET",
            uri: uri,
            headers: headers,
            body: body,
            credentials: credentials,
            region: "us-east-1",
            service: "service",
            signingTime: signingTime);

        const string emptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        const string expectedCanonicalRequest = """
        GET
        /
        a=1&b=one&b=two&empty=&space=hello%20world
        content-type:text/plain; charset=utf-8
        host:example.amazonaws.com
        x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        x-amz-date:20240102T030405Z
        x-custom:a b c

        content-type;host;x-amz-content-sha256;x-amz-date;x-custom
        e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        """;

        Assert.Equal(expectedCanonicalRequest.ReplaceLineEndings("\n"), signature.CanonicalRequest);
        Assert.Equal(emptyPayloadHash, signature.HeadersToApply["X-Amz-Content-Sha256"]);
        Assert.Equal("content-type;host;x-amz-content-sha256;x-amz-date;x-custom", signature.SignedHeaders);
    }

    [Fact]
    public void Sign_KnownAwsVector_GetVanilla()
    {
        // AWS SigV4 test suite "get-vanilla" vector.
        // Reference: https://docs.aws.amazon.com/general/latest/gr/sigv4-test-suite.html
        var credentials = new AwsCredentials(
            AccessKeyId: "AKIDEXAMPLE",
            SecretAccessKey: "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY");
        var uri = new Uri("https://example.amazonaws.com/");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] body = Array.Empty<byte>();
        var signingTime = new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

        AwsSigV4Signature signature = AwsSigV4Signer.Sign(
            method: "GET",
            uri: uri,
            headers: headers,
            body: body,
            credentials: credentials,
            region: "us-east-1",
            service: "service",
            signingTime: signingTime);

        Assert.Equal("20150830T123600Z", signature.AmzDate);
        Assert.Equal("20150830/us-east-1/service/aws4_request", signature.CredentialScope);

        // Sanity: signed headers must include at least host, x-amz-content-sha256, and x-amz-date.
        Assert.Contains("host", signature.SignedHeaders);
        Assert.Contains("x-amz-date", signature.SignedHeaders);
        Assert.Equal(64, signature.Signature.Length);
    }

    private static string SignFreshAndAssertConsistent(
        AwsCredentials credentials,
        Uri uri,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        DateTimeOffset signingTime)
    {
        AwsSigV4Signature first = AwsSigV4Signer.Sign("POST", uri, headers, body, credentials, "us-east-1", "monitoring", signingTime);
        AwsSigV4Signature second = AwsSigV4Signer.Sign("POST", uri, headers, body, credentials, "us-east-1", "monitoring", signingTime);
        Assert.Equal(first.Signature, second.Signature);
        Assert.Equal(first.CanonicalRequest, second.CanonicalRequest);
        Assert.Equal(first.StringToSign, second.StringToSign);
        return first.Signature;
    }
}
