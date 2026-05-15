// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

namespace LlmUsageExporter.Api.Providers.Abstractions;

public interface IRegistrationDecorator
{
    LlmProviderRegistration Decorate(LlmProviderRegistration registration);
}
