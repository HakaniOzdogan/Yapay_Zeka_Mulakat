using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace InterviewCoach.Tests.Helpers;

public static class JsonSnapshotComparer
{
    private static readonly HashSet<string> VolatileFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "id",
        "sessionId",
        "traceId",
        "requestId",
        "exportedAtUtc",
        "createdAt",
        "createdAtUtc",
        "updatedAt",
        "llmRunId"
    };

    public static string NormalizeAndFormat(string json)
    {
        var node = JsonNode.Parse(json) ?? throw new InvalidOperationException("JSON cannot be null.");
        var normalized = Normalize(node, "$");
        return normalized.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static void AssertMatches(string expectedJson, string actualJson)
    {
        var expectedNode = Normalize(JsonNode.Parse(expectedJson) ?? throw new InvalidOperationException("Expected JSON cannot be null."), "$");
        var actualNode = Normalize(JsonNode.Parse(actualJson) ?? throw new InvalidOperationException("Actual JSON cannot be null."), "$");

        var differences = new List<string>();
        Compare(expectedNode, actualNode, "$", differences);

        differences.Should().BeEmpty(
            string.Join(Environment.NewLine, differences.Take(30)));
    }

    private static JsonNode Normalize(JsonNode node, string path)
    {
        return node switch
        {
            JsonObject obj => NormalizeObject(obj, path),
            JsonArray arr => NormalizeArray(arr, path),
            _ => node.DeepClone()
        };
    }

    private static JsonObject NormalizeObject(JsonObject source, string path)
    {
        var target = new JsonObject();
        foreach (var kv in source.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (ShouldIgnoreField(kv.Key))
                continue;

            if (kv.Value == null)
            {
                target[kv.Key] = null;
                continue;
            }

            target[kv.Key] = Normalize(kv.Value, $"{path}.{kv.Key}");
        }

        return target;
    }

    private static JsonArray NormalizeArray(JsonArray source, string path)
    {
        var items = source
            .Select(item => item is null ? null : Normalize(item, $"{path}[]"))
            .ToList();

        if (IsPatternsArray(path))
        {
            items = items
                .OrderBy(PatternSortKey, StringComparer.Ordinal)
                .ToList();
        }

        var target = new JsonArray();
        foreach (var item in items)
        {
            target.Add(item);
        }

        return target;
    }

    private static bool IsPatternsArray(string path) =>
        path.EndsWith(".patterns", StringComparison.OrdinalIgnoreCase);

    private static string PatternSortKey(JsonNode? node)
    {
        if (node is not JsonObject obj)
            return string.Empty;

        var type = obj["type"]?.ToString() ?? string.Empty;
        var start = obj["startMs"]?.ToString() ?? obj["start_ms"]?.ToString() ?? "0";
        var end = obj["endMs"]?.ToString() ?? obj["end_ms"]?.ToString() ?? "0";
        var severity = obj["severity"]?.ToString() ?? "0";
        return $"{type}|{start}|{end}|{severity}";
    }

    private static bool ShouldIgnoreField(string fieldName)
    {
        if (VolatileFields.Contains(fieldName))
            return true;

        return fieldName.Contains("createdAt", StringComparison.OrdinalIgnoreCase)
               || fieldName.Contains("updatedAt", StringComparison.OrdinalIgnoreCase)
               || fieldName.Equals("exportedAt", StringComparison.OrdinalIgnoreCase);
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string path, List<string> differences)
    {
        if (expected is null && actual is null)
            return;

        if (expected is null || actual is null)
        {
            differences.Add($"{path}: expected {(expected is null ? "null" : expected.ToJsonString())}, actual {(actual is null ? "null" : actual.ToJsonString())}");
            return;
        }

        if (expected.GetType() != actual.GetType())
        {
            differences.Add($"{path}: expected type {expected.GetType().Name}, actual type {actual.GetType().Name}");
            return;
        }

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject)
        {
            var expectedKeys = expectedObject.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var actualKeys = actualObject.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();

            if (!expectedKeys.SequenceEqual(actualKeys))
            {
                differences.Add($"{path}: object keys differ. expected=[{string.Join(", ", expectedKeys)}], actual=[{string.Join(", ", actualKeys)}]");
                return;
            }

            foreach (var key in expectedKeys)
            {
                Compare(expectedObject[key], actualObject[key], $"{path}.{key}", differences);
            }

            return;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray)
        {
            if (expectedArray.Count != actualArray.Count)
            {
                differences.Add($"{path}: array length differs. expected={expectedArray.Count}, actual={actualArray.Count}");
                return;
            }

            for (var i = 0; i < expectedArray.Count; i++)
            {
                Compare(expectedArray[i], actualArray[i], $"{path}[{i}]", differences);
            }

            return;
        }

        if (expected is JsonValue expectedValue && actual is JsonValue actualValue)
        {
            if (TryGetNumber(expectedValue, out var expectedNumber) && TryGetNumber(actualValue, out var actualNumber))
            {
                var tolerance = GetTolerance(path);
                if (Math.Abs(expectedNumber - actualNumber) > tolerance)
                {
                    differences.Add($"{path}: expected {expectedNumber.ToString(CultureInfo.InvariantCulture)}, actual {actualNumber.ToString(CultureInfo.InvariantCulture)}, tolerance={tolerance.ToString(CultureInfo.InvariantCulture)}");
                }

                return;
            }

            var expectedText = expectedValue.ToJsonString();
            var actualText = actualValue.ToJsonString();
            if (!string.Equals(expectedText, actualText, StringComparison.Ordinal))
            {
                differences.Add($"{path}: expected {expectedText}, actual {actualText}");
            }
        }
    }

    private static bool TryGetNumber(JsonValue value, out double number)
    {
        try
        {
            number = value.GetValue<double>();
            return true;
        }
        catch
        {
            number = 0;
            return false;
        }
    }

    private static double GetTolerance(string path)
    {
        if (path.StartsWith("$.scoreCard.", StringComparison.OrdinalIgnoreCase))
            return 3;

        if (path.StartsWith("$.signals.", StringComparison.OrdinalIgnoreCase))
            return 0.05;

        if (path.Equals("$.highLevel.overallScore", StringComparison.OrdinalIgnoreCase))
            return 3;

        return 0;
    }
}
