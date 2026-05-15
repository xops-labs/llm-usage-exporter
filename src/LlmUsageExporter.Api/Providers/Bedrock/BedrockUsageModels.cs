// Copyright 2026 llm-usage-exporter Contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Xml.Linq;

namespace LlmUsageExporter.Api.Providers.Bedrock;

public sealed record BedrockUsageQuery(
    DateTimeOffset Start,
    DateTimeOffset End,
    IReadOnlyCollection<string> ModelIds,
    int PeriodSeconds);

public sealed record BedrockMetricDataResult(
    string Id,
    string Label,
    IReadOnlyList<DateTimeOffset> Timestamps,
    IReadOnlyList<double> Values);

public static class BedrockCloudWatchResponseParser
{
    public static IReadOnlyList<BedrockMetricDataResult> Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return Array.Empty<BedrockMetricDataResult>();
        }

        XDocument document = XDocument.Parse(xml);
        XNamespace? defaultNamespace = document.Root?.GetDefaultNamespace();

        // The CloudWatch GetMetricData response wraps each result in a <member> element under <MetricDataResults>.
        // Each member contains the result fields directly (Id, Label, Timestamps, Values).
        XName metricDataResultsName = defaultNamespace is null ? "MetricDataResults" : defaultNamespace + "MetricDataResults";
        XName memberName = defaultNamespace is null ? "member" : defaultNamespace + "member";

        List<BedrockMetricDataResult> results = [];
        foreach (XElement membersParent in document.Descendants(metricDataResultsName))
        foreach (XElement metricResult in membersParent.Elements(memberName))
        {
            string id = GetChildValue(metricResult, "Id", defaultNamespace) ?? string.Empty;
            string label = GetChildValue(metricResult, "Label", defaultNamespace) ?? string.Empty;

            List<DateTimeOffset> timestamps = [];
            XElement? timestampsParent = GetChild(metricResult, "Timestamps", defaultNamespace);
            if (timestampsParent is not null)
            {
                foreach (XElement member in EnumerateMembers(timestampsParent, defaultNamespace))
                {
                    if (DateTimeOffset.TryParse(
                        member.Value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset parsed))
                    {
                        timestamps.Add(parsed);
                    }
                }
            }

            List<double> values = [];
            XElement? valuesParent = GetChild(metricResult, "Values", defaultNamespace);
            if (valuesParent is not null)
            {
                foreach (XElement member in EnumerateMembers(valuesParent, defaultNamespace))
                {
                    if (double.TryParse(member.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    {
                        values.Add(parsed);
                    }
                }
            }

            results.Add(new BedrockMetricDataResult(id, label, timestamps, values));
        }

        return results;
    }

    private static XElement? GetChild(XElement parent, string name, XNamespace? defaultNamespace)
    {
        XName resolved = defaultNamespace is null ? name : defaultNamespace + name;
        return parent.Element(resolved);
    }

    private static string? GetChildValue(XElement parent, string name, XNamespace? defaultNamespace)
    {
        return GetChild(parent, name, defaultNamespace)?.Value;
    }

    private static IEnumerable<XElement> EnumerateMembers(XElement parent, XNamespace? defaultNamespace)
    {
        XName memberName = defaultNamespace is null ? "member" : defaultNamespace + "member";
        return parent.Elements(memberName);
    }
}
