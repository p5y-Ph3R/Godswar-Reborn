using System.Text.RegularExpressions;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal sealed record LegacyPersistenceTelemetrySourceScan(
    IReadOnlyDictionary<LegacyPersistenceTelemetryKey, int> Records,
    IReadOnlyList<string> AssociationViolations);

/// <summary>
/// Scans executable-looking C# source only. Trivia and literals are replaced
/// with whitespace of the same length so regex match offsets remain stable.
/// </summary>
internal static class B20LegacyPersistenceTelemetrySourceAnalyzer
{
    private static readonly Regex RecordPattern = new(
        @"\bLegacyPersistenceMetrics\s*\.\s*Record\s*\(\s*" +
        @"LegacyPersistenceOperation\s*\.\s*" +
        @"(?<operation>[A-Za-z][A-Za-z0-9]*)\s*\)",
        RegexOptions.CultureInvariant);

    public static LegacyPersistenceTelemetrySourceScan Scan(
        IReadOnlyDictionary<string, string> serverSource,
        IReadOnlyDictionary<LegacyPersistenceTelemetryKey, int> expected)
    {
        ArgumentNullException.ThrowIfNull(serverSource);
        ArgumentNullException.ThrowIfNull(expected);

        var sanitized = serverSource.ToDictionary(
            static pair => NormalizePath(pair.Key),
            static pair => MaskTriviaAndLiterals(pair.Value),
            StringComparer.Ordinal);
        var records = CountRecords(sanitized);
        var associationViolations = FindAssociationViolations(
            sanitized,
            expected);
        return new LegacyPersistenceTelemetrySourceScan(
            records,
            associationViolations);
    }

    private static Dictionary<LegacyPersistenceTelemetryKey, int>
        CountRecords(IReadOnlyDictionary<string, string> sanitizedSource)
    {
        var result = new Dictionary<
            LegacyPersistenceTelemetryKey,
            int>();
        foreach (var (path, source) in sanitizedSource)
        {
            foreach (Match match in RecordPattern.Matches(source))
            {
                var value = match.Groups["operation"].Value;
                if (!Enum.TryParse<LegacyPersistenceOperation>(
                        value,
                        ignoreCase: false,
                        out var operation) ||
                    !Enum.IsDefined(operation))
                {
                    throw new InvalidDataException(
                        $"Unknown legacy metric operation {value} in " +
                        $"{path}.");
                }

                var key = new LegacyPersistenceTelemetryKey(
                    path,
                    operation);
                result.TryGetValue(key, out var count);
                result[key] = checked(count + 1);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> FindAssociationViolations(
        IReadOnlyDictionary<string, string> sanitizedSource,
        IReadOnlyDictionary<LegacyPersistenceTelemetryKey, int> expected)
    {
        var violations = new List<string>();
        foreach (var (key, required) in expected)
        {
            sanitizedSource.TryGetValue(key.Path, out var source);
            source ??= string.Empty;
            var records = RecordPattern.Matches(source)
                .Where(match => string.Equals(
                    match.Groups["operation"].Value,
                    key.Operation.ToString(),
                    StringComparison.Ordinal))
                .ToArray();
            var invocations = InvocationPattern(key.Operation)
                .Matches(source)
                .ToArray();

            if (invocations.Length != required)
            {
                violations.Add(
                    $"path={key.Path} operation={key.Operation} " +
                    $"legacy_calls_required={required} " +
                    $"legacy_calls_found={invocations.Length}");
                continue;
            }

            // Missing and excess records are reported by the count ratchet.
            // Association is meaningful only when both sides have exact
            // cardinality.
            if (records.Length != required)
            {
                continue;
            }

            var previousInvocation = -1;
            for (var index = 0; index < required; index++)
            {
                var recordPosition = records[index].Index;
                var invocationPosition = invocations[index].Index;
                if (recordPosition <= previousInvocation ||
                    recordPosition >= invocationPosition)
                {
                    violations.Add(
                        $"path={key.Path} operation={key.Operation} " +
                        $"pair={index + 1} record_offset={recordPosition} " +
                        $"call_offset={invocationPosition}; record must " +
                        "appear after the prior matching call and before " +
                        "this call");
                }

                previousInvocation = invocationPosition;
            }
        }

        return violations.Order(StringComparer.Ordinal).ToArray();
    }

    private static Regex InvocationPattern(
        LegacyPersistenceOperation operation) =>
        new(
            @"\b(?:_gameStore|_store|store|jsonStore)\s*" +
            @"(?:[!?]\s*)?\.\s*" +
            Regex.Escape(operation + "Async") +
            @"\s*\(",
            RegexOptions.CultureInvariant);

    private static string MaskTriviaAndLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var masked = source.ToCharArray();
        for (var index = 0; index < source.Length;)
        {
            if (StartsWith(source, index, "//"))
            {
                var end = source.IndexOf('\n', index + 2);
                end = end < 0 ? source.Length : end;
                Mask(masked, index, end);
                index = end;
                continue;
            }

            if (StartsWith(source, index, "/*"))
            {
                var close = source.IndexOf(
                    "*/",
                    index + 2,
                    StringComparison.Ordinal);
                var end = close < 0 ? source.Length : close + 2;
                Mask(masked, index, end);
                index = end;
                continue;
            }

            if (TryFindStringEnd(source, index, out var stringEnd))
            {
                Mask(masked, index, stringEnd);
                index = stringEnd;
                continue;
            }

            if (source[index] == '\'')
            {
                var end = FindQuotedEnd(
                    source,
                    index + 1,
                    '\'',
                    verbatim: false);
                Mask(masked, index, end);
                index = end;
                continue;
            }

            index++;
        }

        return new string(masked);
    }

    private static bool TryFindStringEnd(
        string source,
        int start,
        out int end)
    {
        end = start;
        var quote = -1;
        var verbatim = false;
        if (source[start] == '"')
        {
            quote = start;
        }
        else if (source[start] == '@')
        {
            var next = start + 1;
            if (next < source.Length && source[next] == '"')
            {
                quote = next;
                verbatim = true;
            }
            else if (next < source.Length && source[next] == '$')
            {
                while (next < source.Length && source[next] == '$')
                {
                    next++;
                }
                if (next < source.Length && source[next] == '"')
                {
                    quote = next;
                    verbatim = true;
                }
            }
        }
        else if (source[start] == '$')
        {
            var next = start;
            while (next < source.Length && source[next] == '$')
            {
                next++;
            }
            if (next < source.Length && source[next] == '@')
            {
                verbatim = true;
                next++;
            }
            if (next < source.Length && source[next] == '"')
            {
                quote = next;
            }
        }

        if (quote < 0)
        {
            return false;
        }

        var quoteCount = CountRun(source, quote, '"');
        end = quoteCount >= 3
            ? FindRawStringEnd(source, quote, quoteCount)
            : FindQuotedEnd(
                source,
                quote + 1,
                '"',
                verbatim);
        return true;
    }

    private static int FindRawStringEnd(
        string source,
        int quote,
        int delimiterLength)
    {
        for (var index = quote + delimiterLength;
             index < source.Length;)
        {
            if (source[index] != '"')
            {
                index++;
                continue;
            }

            var run = CountRun(source, index, '"');
            if (run >= delimiterLength)
            {
                return index + run;
            }
            index += run;
        }

        return source.Length;
    }

    private static int FindQuotedEnd(
        string source,
        int contentStart,
        char delimiter,
        bool verbatim)
    {
        for (var index = contentStart; index < source.Length; index++)
        {
            if (!verbatim && source[index] == '\\')
            {
                index++;
                continue;
            }
            if (source[index] != delimiter)
            {
                continue;
            }
            if (verbatim &&
                index + 1 < source.Length &&
                source[index + 1] == delimiter)
            {
                index++;
                continue;
            }
            return index + 1;
        }

        return source.Length;
    }

    private static int CountRun(string source, int start, char value)
    {
        var index = start;
        while (index < source.Length && source[index] == value)
        {
            index++;
        }
        return index - start;
    }

    private static bool StartsWith(
        string source,
        int index,
        string value) =>
        index + value.Length <= source.Length &&
        source.AsSpan(index, value.Length).SequenceEqual(value);

    private static void Mask(char[] destination, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (destination[index] is not ('\r' or '\n'))
            {
                destination[index] = ' ';
            }
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
