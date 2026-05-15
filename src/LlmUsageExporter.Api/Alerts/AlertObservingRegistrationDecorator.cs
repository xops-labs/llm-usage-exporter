// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using LlmUsageExporter.Api.Providers.Abstractions;

namespace LlmUsageExporter.Api.Alerts;

public sealed class AlertObservingRegistrationDecorator : IRegistrationDecorator
{
    private readonly IAlertSource _source;

    public AlertObservingRegistrationDecorator(IAlertSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public LlmProviderRegistration Decorate(LlmProviderRegistration registration)
    {
        if (registration.Publisher is AlertObservingPublisher)
        {
            return registration;
        }

        return registration with
        {
            Publisher = new AlertObservingPublisher(registration.Publisher, _source),
        };
    }
}
