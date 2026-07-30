using System.Text.RegularExpressions;

namespace Godswar.Server.Operations.Observability;

internal enum OperationalLogLevel : byte
{
    Trace = 1,
    Debug = 2,
    Information = 3,
    Warning = 4,
    Error = 5,
    Critical = 6
}

internal enum OperationalLogEvent : byte
{
    ServerLifecycle = 1,
    CriticalTaskState = 2,
    ReadinessChanged = 3,
    ManagementRequest = 4,
    TelemetryExporter = 5,
    LegacyDiagnosticSuppressed = 6
}

internal enum OperationalLogField : byte
{
    Component = 1,
    Outcome = 2,
    Reason = 3,
    State = 4,
    Source = 5,
    Category = 6,
    Count = 7,
    DurationMilliseconds = 8,
    Truncated = 9
}

internal enum OperationalLogValueKind : byte
{
    Code = 1,
    Number = 2,
    Boolean = 3
}

internal readonly record struct OperationalLogValue
{
    private OperationalLogValue(
        OperationalLogField field,
        OperationalLogValueKind kind,
        string? code,
        long number,
        bool boolean)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        Field = field;
        Kind = kind;
        Code = code;
        Number = number;
        Boolean = boolean;
    }

    public OperationalLogField Field { get; }

    public OperationalLogValueKind Kind { get; }

    public string? Code { get; }

    public long Number { get; }

    public bool Boolean { get; }

    public static OperationalLogValue FromCode(
        OperationalLogField field,
        string code) =>
        new(
            field,
            OperationalLogValueKind.Code,
            SafeTelemetryCode.Require(code, nameof(code)),
            0,
            false);

    public static OperationalLogValue FromNumber(
        OperationalLogField field,
        long value) =>
        new(
            field,
            OperationalLogValueKind.Number,
            null,
            value,
            false);

    public static OperationalLogValue FromBoolean(
        OperationalLogField field,
        bool value) =>
        new(
            field,
            OperationalLogValueKind.Boolean,
            null,
            0,
            value);
}

internal sealed class StructuredLogOptions
{
    public const int MaximumFields = 8;

    public int MaximumLineBytes { get; init; } = 4_096;

    public int GlobalEventsPerWindow { get; init; } = 2_048;

    public int EventsPerKindPerWindow { get; init; } = 256;

    public TimeSpan RateWindow { get; init; } = TimeSpan.FromMinutes(1);

    public int MaximumLegacyCharactersPerWrite { get; init; } = 4_096;

    public int QueueCapacity { get; init; } = 1_024;

    public TimeSpan ShutdownTimeout { get; init; } =
        TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (MaximumLineBytes is < 256 or > 16_384)
        {
            throw new InvalidDataException(
                "Structured log lines must be bounded from 256 to 16384 bytes.");
        }
        if (GlobalEventsPerWindow is < 1 or > 1_000_000)
        {
            throw new InvalidDataException(
                "The global structured-log rate bound is invalid.");
        }
        if (EventsPerKindPerWindow is < 1 or > 100_000 ||
            EventsPerKindPerWindow > GlobalEventsPerWindow)
        {
            throw new InvalidDataException(
                "The per-event structured-log rate bound is invalid.");
        }
        if (RateWindow < TimeSpan.FromSeconds(1) ||
            RateWindow > TimeSpan.FromHours(1))
        {
            throw new InvalidDataException(
                "The structured-log rate window is invalid.");
        }
        if (MaximumLegacyCharactersPerWrite is < 64 or > 65_536)
        {
            throw new InvalidDataException(
                "The legacy-console write bound is invalid.");
        }
        if (QueueCapacity is < 1 or > 65_536)
        {
            throw new InvalidDataException(
                "The structured-log queue capacity is invalid.");
        }
        if (ShutdownTimeout < TimeSpan.FromMilliseconds(10) ||
            ShutdownTimeout > TimeSpan.FromSeconds(30))
        {
            throw new InvalidDataException(
                "The structured-log shutdown timeout is invalid.");
        }
    }
}

internal readonly record struct StructuredLogRuntimeSnapshot(
    long Accepted,
    long Written,
    long QueueFull,
    long RateLimited,
    long Oversized,
    long SinkFailures,
    long LegacyLinesSuppressed,
    long ShutdownDropped);

internal static partial class SafeTelemetryCode
{
    internal const int MaximumLength = 64;

    [GeneratedRegex(
        "^[a-z0-9](?:[a-z0-9_.-]{0,63})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > MaximumLength ||
            !Pattern().IsMatch(value))
        {
            throw new ArgumentException(
                "Telemetry codes must use at most 64 lowercase ASCII code characters.",
                parameterName);
        }

        return value;
    }

    public static bool IsSafe(string? value) =>
        value is not null &&
        value.Length <= MaximumLength &&
        Pattern().IsMatch(value);
}
