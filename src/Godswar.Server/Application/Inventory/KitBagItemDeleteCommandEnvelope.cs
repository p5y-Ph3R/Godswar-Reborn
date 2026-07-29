using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct KitBagItemDeleteCommand(
    Guid ClientOperationId,
    int KitBagSlot,
    string ExpectedCompactItemState);

internal static class KitBagItemDeleteCommandEnvelope
{
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int CanonicalPrefixBytes =
        sizeof(ushort) + sizeof(ushort) + sizeof(ushort);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int kitBagSlot,
        string? expectedCompactItemState,
        out KitBagItemDeleteCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty ||
            kitBagSlot is < MinimumKitBagSlot or > MaximumKitBagSlot ||
            !TryGetExpectedStateBytes(
                expectedCompactItemState,
                out _))
        {
            return false;
        }

        command = new KitBagItemDeleteCommand(
            clientOperationId,
            kitBagSlot,
            expectedCompactItemState!);
        return true;
    }

    public static CommandEnvelope<KitBagItemDeleteCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        KitBagItemDeleteCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The kit-bag item-delete command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Kit-bag item deletion requires authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            CommandFamily.KitBagItemDelete,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<KitBagItemDeleteCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!IsTrustedTransport(envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(
            envelope.Command.ClientOperationId,
            operationScope);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.KitBagItemDelete,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        Guid clientOperationId)
    {
        if (clientOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A non-empty client operation ID is required.",
                nameof(clientOperationId));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            CommandFamily.KitBagItemDelete,
            subject,
            operationScope);
    }

    private static bool IsValidCommand(
        KitBagItemDeleteCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        command.KitBagSlot is
            >= MinimumKitBagSlot and <= MaximumKitBagSlot &&
        TryGetExpectedStateBytes(
            command.ExpectedCompactItemState,
            out _);

    private static bool IsTrustedTransport(
        CommandTransportKind transport) =>
        transport is
            CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    private static bool TryGetExpectedStateBytes(
        string? value,
        out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']')
        {
            return false;
        }

        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return bytes.Length <= MaximumExpectedStateUtf8Bytes &&
                CanonicalPrefixBytes + bytes.Length <=
                CommandEnvelopeContract.MaximumCanonicalRequestBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static byte[] CreateCanonicalRequest(
        KitBagItemDeleteCommand command)
    {
        if (!TryGetExpectedStateBytes(
                command.ExpectedCompactItemState,
                out var expectedState))
        {
            throw new ArgumentException(
                "The expected item state cannot be encoded.",
                nameof(command));
        }

        var canonical = new byte[CanonicalPrefixBytes +
            expectedState.Length];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[sizeof(ushort)..],
            checked((ushort)command.KitBagSlot));
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[(sizeof(ushort) * 2)..],
            checked((ushort)expectedState.Length));
        expectedState.CopyTo(destination[CanonicalPrefixBytes..]);
        return canonical;
    }

    private static void WriteOperationScope(
        Guid clientOperationId,
        Span<byte> destination)
    {
        if (!clientOperationId.TryWriteBytes(
                destination,
                bigEndian: true,
                out var bytesWritten) ||
            bytesWritten != OperationScopeBytes)
        {
            throw new ArgumentException(
                "The operation ID could not be encoded.",
                nameof(clientOperationId));
        }
    }
}
