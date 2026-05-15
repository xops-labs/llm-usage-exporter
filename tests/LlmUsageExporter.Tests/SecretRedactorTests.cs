// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Security;

namespace LlmUsageExporter.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_RemovesKnownSecretsAndCredentialFields()
    {
        const string knownClientSecret = "azure-secret-value";
        const string knownAwsSecret = "aws-secret-value";
        const string knownAnthropicKey = "sk-ant-admin-secret";
        string source = $$"""
        {
          "client_secret": "{{knownClientSecret}}",
          "AWS_ACCESS_KEY_ID": "AKIAIOSFODNN7EXAMPLE",
          "access_token": "provider-access-token",
          "private_key": "-----BEGIN PRIVATE KEY----- top secret -----END PRIVATE KEY-----",
          "message": "Authorization: Bearer sk-admin-secret x-api-key={{knownAnthropicKey}} AWS_SECRET_ACCESS_KEY={{knownAwsSecret}}"
        }
        """;

        string redacted = SecretRedactor.Redact(source, knownClientSecret, knownAwsSecret, knownAnthropicKey);

        Assert.DoesNotContain(knownClientSecret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(knownAwsSecret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(knownAnthropicKey, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-access-token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-admin-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("top secret", redacted, StringComparison.Ordinal);
        Assert.Contains("[redacted]", redacted, StringComparison.Ordinal);
    }
}
