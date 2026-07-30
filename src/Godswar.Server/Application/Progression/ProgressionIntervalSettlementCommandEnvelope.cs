using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Progression;

internal readonly record struct ProgressionIntervalSettlementCommand(
    Guid OnlineSessionId,
    long IntervalSequence,
    DateTimeOffset OnlineFromUtc,
    DateTimeOffset OnlineUntilUtc);

internal static class ProgressionIntervalSettlementCommandEnvelope
{
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromHours(24);
    public static readonly DateTimeOffset MinimumUtc =
        new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset MaximumUtcExclusive =
        new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static CommandEnvelope<ProgressionIntervalSettlementCommand> Create(
        CommandSubject subject,
        Guid onlineSessionId,
        long intervalSequence,
        DateTimeOffset onlineFromUtc,
        DateTimeOffset onlineUntilUtc,
        CommandTransportKind transport)
    {
        if (onlineFromUtc.Offset != TimeSpan.Zero ||
            onlineUntilUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(onlineUntilUtc),
                "Progression interval endpoints must be UTC.");
        }

        onlineFromUtc = CanonicalizeUtc(onlineFromUtc);
        onlineUntilUtc = CanonicalizeUtc(onlineUntilUtc);
        var command = new ProgressionIntervalSettlementCommand(
            onlineSessionId,
            intervalSequence,
            onlineFromUtc,
            onlineUntilUtc);
        if (!IsValidCommand(command) || !Enum.IsDefined(transport))
        {
            throw new ArgumentOutOfRangeException(
                nameof(onlineUntilUtc),
                "The server progression interval is invalid.");
        }

        Span<byte> operationScope = stackalloc byte[24];
        WriteOperationIdentity(command, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[40];
        WriteCanonicalRequest(command, canonicalRequest);
        return CommandEnvelopeContract.Create(
            CommandFamily.ProgressionIntervalSettlement,
            CommandIdentityStrength.ServerOperationId,
            subject,
            new CommandConnectionCorrelation(
                onlineSessionId,
                transport),
            onlineUntilUtc,
            operationScope,
            canonicalRequest,
            command);
    }

    internal static DateTimeOffset CanonicalizeUtc(
        DateTimeOffset value)
    {
        var utcTicks = value.UtcDateTime.Ticks;
        var postgresTicks =
            utcTicks - (utcTicks % (TimeSpan.TicksPerMillisecond / 1_000));
        return new DateTimeOffset(postgresTicks, TimeSpan.Zero);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<ProgressionIntervalSettlementCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command) ||
            envelope.Connection.ConnectionId !=
                envelope.Command.OnlineSessionId ||
            envelope.ReceivedAt != envelope.Command.OnlineUntilUtc)
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        Span<byte> operationScope = stackalloc byte[24];
        WriteOperationIdentity(envelope.Command, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[40];
        WriteCanonicalRequest(envelope.Command, canonicalRequest);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.ProgressionIntervalSettlement,
            CommandIdentityStrength.ServerOperationId,
            operationScope,
            canonicalRequest);
    }

    private static bool IsValidCommand(
        ProgressionIntervalSettlementCommand command)
    {
        if (command.OnlineSessionId == Guid.Empty ||
            command.IntervalSequence <= 0 ||
            command.OnlineFromUtc == default ||
            command.OnlineUntilUtc == default ||
            command.OnlineFromUtc.Offset != TimeSpan.Zero ||
            command.OnlineUntilUtc.Offset != TimeSpan.Zero ||
            command.OnlineFromUtc.UtcDateTime.Ticks % 10 != 0 ||
            command.OnlineUntilUtc.UtcDateTime.Ticks % 10 != 0 ||
            command.OnlineFromUtc < MinimumUtc ||
            command.OnlineUntilUtc >= MaximumUtcExclusive ||
            command.OnlineUntilUtc <= command.OnlineFromUtc)
        {
            return false;
        }

        var duration =
            command.OnlineUntilUtc - command.OnlineFromUtc;
        return duration <= MaximumInterval;
    }

    private static void WriteOperationIdentity(
        ProgressionIntervalSettlementCommand command,
        Span<byte> destination)
    {
        if (destination.Length != 24)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        command.OnlineSessionId.TryWriteBytes(destination[..16]);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[16..],
            command.IntervalSequence);
    }

    private static void WriteCanonicalRequest(
        ProgressionIntervalSettlementCommand command,
        Span<byte> destination)
    {
        if (destination.Length != 40)
        {
            throw new ArgumentOutOfRangeException(nameof(destination));
        }

        WriteOperationIdentity(command, destination[..24]);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[24..32],
            command.OnlineFromUtc.UtcDateTime.Ticks);
        BinaryPrimitives.WriteInt64BigEndian(
            destination[32..],
            command.OnlineUntilUtc.UtcDateTime.Ticks);
    }
}
