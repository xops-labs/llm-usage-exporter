// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Focus;

public sealed class FocusObservingRegistrationDecorator : IRegistrationDecorator
{
    private readonly IFocusRecordStore _store;

    public FocusObservingRegistrationDecorator(IFocusRecordStore store)
    {
        _store = store;
    }

    public LlmProviderRegistration Decorate(LlmProviderRegistration registration)
    {
        return registration with
        {
            Publisher = new FocusObservingPublisher(registration.Publisher, _store)
        };
    }
}
