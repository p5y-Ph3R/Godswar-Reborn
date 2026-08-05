using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal static partial class HolyStoneCommandEnvelope
{
    public static CommandEnvelope<HolyStoneCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolyStoneCommand command)
    {
        if (!IsValidCommand(command) ||
            !command.Identity.IsSecureClient ||
            !IsSecureTransport(connection.Transport))
        {
            throw new ArgumentException(
                "The secure Holy Stone command or its provenance is invalid.",
                nameof(command));
        }

        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<HolyStoneCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolyStoneCommand command)
    {
        if (!IsValidCommand(command) ||
            !command.Identity.IsRawLocalServer ||
            !SupportsRawLocalIdentity(command.Operation) ||
            connection.Transport != CommandTransportKind.LegacyTcp ||
            command.Identity.RawLocalConnectionId != connection.ConnectionId)
        {
            throw new ArgumentException(
                "Raw-local Holy Stone commands require a server operation " +
                "identity scoped to the exact legacy connection.",
                nameof(command));
        }

        return CreateCore(subject, connection, receivedAt, command);
    }

    private static CommandEnvelope<HolyStoneCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolyStoneCommand command) =>
        CommandEnvelopeContract.Create(
            Family(command.Operation),
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            CreateOperationScope(command.Identity),
            CreateCanonicalRequest(command),
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<HolyStoneCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command) ||
            (envelope.Command.Identity.IsRawLocalServer &&
             !SupportsRawLocalIdentity(envelope.Command.Operation)))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            Family(envelope.Command.Operation),
            envelope.Command.Identity.Strength,
            CreateOperationScope(envelope.Command.Identity),
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        HolyStoneCommandOperation operation,
        Guid clientOperationId)
    {
        if (!Enum.IsDefined(operation) || clientOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A supported operation and non-empty UUID are required.");
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteGuid(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            Family(operation),
            subject,
            operationScope);
    }

    public static string CreateOperationId(
        CommandSubject subject,
        HolyStoneCommandOperation operation,
        HolyStoneOperationIdentity identity)
    {
        if (!Enum.IsDefined(operation) ||
            !IsValidIdentity(identity) ||
            (identity.IsRawLocalServer &&
             !SupportsRawLocalIdentity(operation)))
        {
            throw new ArgumentException(
                "A supported operation and bounded identity are required.");
        }

        return CommandEnvelopeContract.DeriveOperationId(
            Family(operation),
            subject,
            CreateOperationScope(identity));
    }

    private static bool IsValidIdentity(
        HolyStoneOperationIdentity identity) =>
        identity.IsSecureClient || identity.IsRawLocalServer;

    private static bool HasMatchingProvenance(
        HolyStoneOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient
            ? IsSecureTransport(connection.Transport)
            : identity.IsRawLocalServer &&
              connection.Transport == CommandTransportKind.LegacyTcp &&
              identity.RawLocalConnectionId == connection.ConnectionId;

    private static bool IsSecureTransport(
        CommandTransportKind transport) =>
        transport is
            CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    private static byte[] CreateOperationScope(
        HolyStoneOperationIdentity identity)
    {
        if (identity.IsSecureClient)
        {
            var secure = new byte[OperationScopeBytes];
            WriteGuid(identity.OperationId, secure);
            return secure;
        }
        if (!identity.IsRawLocalServer)
        {
            throw new ArgumentException(
                "The Holy Stone operation identity is invalid.",
                nameof(identity));
        }

        var raw = new byte[1 + (OperationScopeBytes * 2)];
        raw[0] = (byte)identity.Strength;
        WriteGuid(identity.OperationId, raw.AsSpan(1, OperationScopeBytes));
        WriteGuid(
            identity.RawLocalConnectionId,
            raw.AsSpan(1 + OperationScopeBytes, OperationScopeBytes));
        return raw;
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != OperationScopeBytes)
        {
            throw new ArgumentException(
                "The operation UUID could not be encoded.",
                nameof(value));
        }
    }
}
