namespace Godswar.Server.Operations.Observability;

internal enum ServerTraceOperation : byte
{
    LoginPacket = 1,
    GamePacket = 2,
    ApplicationCommand = 3,
    PostgresTransaction = 4,
    EcsProjection = 5,
    OutboxDispatch = 6,
    ClientAcknowledgement = 7,
    CheckpointWrite = 8,
    ManagementRequest = 9
}

internal enum ServerTraceTag : byte
{
    Component = 1,
    Outcome = 2,
    Reason = 3,
    State = 4,
    Endpoint = 5,
    Transport = 6,
    CommandFamily = 7,
    Stage = 8,
    Retry = 9,
    Duplicate = 10
}

internal enum ServerTraceValueKind : byte
{
    Code = 1,
    Number = 2,
    Boolean = 3
}

internal enum ServerTraceOutcome : byte
{
    Accepted = 1,
    Rejected = 2,
    Duplicate = 3,
    Cancelled = 4,
    Faulted = 5
}

internal readonly record struct ServerTraceAttribute
{
    private ServerTraceAttribute(
        ServerTraceTag tag,
        ServerTraceValueKind kind,
        string? code,
        long number,
        bool boolean)
    {
        if (!Enum.IsDefined(tag))
        {
            throw new ArgumentOutOfRangeException(nameof(tag));
        }

        Tag = tag;
        Kind = kind;
        Code = code;
        Number = number;
        Boolean = boolean;
    }

    public ServerTraceTag Tag { get; }

    public ServerTraceValueKind Kind { get; }

    public string? Code { get; }

    public long Number { get; }

    public bool Boolean { get; }

    public static ServerTraceAttribute FromCode(
        ServerTraceTag tag,
        string code) =>
        new(
            tag,
            ServerTraceValueKind.Code,
            SafeTelemetryCode.Require(code, nameof(code)),
            0,
            false);

    public static ServerTraceAttribute FromNumber(
        ServerTraceTag tag,
        long value) =>
        new(
            tag,
            ServerTraceValueKind.Number,
            null,
            value,
            false);

    public static ServerTraceAttribute FromBoolean(
        ServerTraceTag tag,
        bool value) =>
        new(
            tag,
            ServerTraceValueKind.Boolean,
            null,
            0,
            value);
}

internal sealed class BoundedTraceBufferOptions
{
    public int Capacity { get; init; } = 1_024;

    public int MaximumTagsPerSpan { get; init; } = 8;

    public void Validate()
    {
        if (Capacity is < 1 or > 4_096)
        {
            throw new InvalidDataException(
                "The trace-buffer capacity is invalid.");
        }
        if (MaximumTagsPerSpan is < 0 or >
            StructuredLogOptions.MaximumFields)
        {
            throw new InvalidDataException(
                "The trace tag bound is invalid.");
        }
    }
}

internal readonly record struct CapturedTraceTag(
    string Name,
    string Value);

internal sealed record CapturedTraceSpan(
    string Operation,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    string Status,
    CapturedTraceTag[] Tags);

internal readonly record struct BoundedTraceBufferRuntimeSnapshot(
    int Capacity,
    int Count,
    long Captured,
    long Overwritten,
    long RejectedTags);

internal static class ServerTraceCodes
{
    public static string Operation(ServerTraceOperation operation) =>
        operation switch
        {
            ServerTraceOperation.LoginPacket => "login.packet",
            ServerTraceOperation.GamePacket => "game.packet",
            ServerTraceOperation.ApplicationCommand =>
                "application.command",
            ServerTraceOperation.PostgresTransaction =>
                "postgres.transaction",
            ServerTraceOperation.EcsProjection => "ecs.projection",
            ServerTraceOperation.OutboxDispatch => "outbox.dispatch",
            ServerTraceOperation.ClientAcknowledgement =>
                "client.acknowledgement",
            ServerTraceOperation.CheckpointWrite => "checkpoint.write",
            ServerTraceOperation.ManagementRequest =>
                "management.request",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static string Tag(ServerTraceTag tag) =>
        tag switch
        {
            ServerTraceTag.Component => "component",
            ServerTraceTag.Outcome => "outcome",
            ServerTraceTag.Reason => "reason",
            ServerTraceTag.State => "state",
            ServerTraceTag.Endpoint => "endpoint",
            ServerTraceTag.Transport => "transport",
            ServerTraceTag.CommandFamily => "command.family",
            ServerTraceTag.Stage => "stage",
            ServerTraceTag.Retry => "retry",
            ServerTraceTag.Duplicate => "duplicate",
            _ => throw new ArgumentOutOfRangeException(nameof(tag))
        };

    public static string Outcome(ServerTraceOutcome outcome) =>
        outcome switch
        {
            ServerTraceOutcome.Accepted => "accepted",
            ServerTraceOutcome.Rejected => "rejected",
            ServerTraceOutcome.Duplicate => "duplicate",
            ServerTraceOutcome.Cancelled => "cancelled",
            ServerTraceOutcome.Faulted => "faulted",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}
