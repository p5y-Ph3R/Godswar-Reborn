using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct KitBagItemMoveCommand(
    Guid ClientOperationId,
    int SourceKitBagSlot,
    int DestinationKitBagSlot,
    string ExpectedSourceCompactItemState,
    string ExpectedDestinationCompactItemState);

internal static class KitBagItemMoveCommandEnvelope
{
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const byte SourceStateRole = 1;
    private const byte DestinationStateRole = 2;
    private const int StateDigestBytes = 32;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + (sizeof(ushort) * 2) + StateDigestBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int sourceKitBagSlot,
        int destinationKitBagSlot,
        string? expectedSourceCompactItemState,
        string? expectedDestinationCompactItemState,
        out KitBagItemMoveCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty ||
            !AreValidDistinctSlots(
                sourceKitBagSlot,
                destinationKitBagSlot) ||
            !TryGetStateBytes(
                expectedSourceCompactItemState,
                out _) ||
            !TryGetStateBytes(
                expectedDestinationCompactItemState,
                out _))
        {
            return false;
        }

        command = new KitBagItemMoveCommand(
            clientOperationId,
            sourceKitBagSlot,
            destinationKitBagSlot,
            expectedSourceCompactItemState!,
            expectedDestinationCompactItemState!);
        return true;
    }

    public static CommandEnvelope<KitBagItemMoveCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        KitBagItemMoveCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The kit-bag item-move command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Kit-bag item movement requires authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            CommandFamily.KitBagItemMove,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<KitBagItemMoveCommand> envelope)
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
            CommandFamily.KitBagItemMove,
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
            CommandFamily.KitBagItemMove,
            subject,
            operationScope);
    }

    internal static byte[] CreateCanonicalRequest(
        KitBagItemMoveCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The kit-bag item-move command is invalid.",
                nameof(command));
        }

        var source = StrictUtf8.GetBytes(
            command.ExpectedSourceCompactItemState);
        var destination = StrictUtf8.GetBytes(
            command.ExpectedDestinationCompactItemState);
        var stateDigest = ComputeStateDigest(source, destination);
        var canonical = new byte[CanonicalRequestBytes];
        BinaryPrimitives.WriteUInt16BigEndian(
            canonical,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            canonical.AsSpan(sizeof(ushort)),
            checked((ushort)command.SourceKitBagSlot));
        BinaryPrimitives.WriteUInt16BigEndian(
            canonical.AsSpan(sizeof(ushort) * 2),
            checked((ushort)command.DestinationKitBagSlot));
        stateDigest.CopyTo(
            canonical.AsSpan(sizeof(ushort) * 3));
        return canonical;
    }

    private static byte[] ComputeStateDigest(
        byte[] source,
        byte[] destination)
    {
        var tagged = new byte[
            1 + sizeof(ushort) + source.Length +
            1 + sizeof(ushort) + destination.Length];
        var offset = 0;
        tagged[offset++] = SourceStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            tagged.AsSpan(offset),
            checked((ushort)source.Length));
        offset += sizeof(ushort);
        source.CopyTo(tagged.AsSpan(offset));
        offset += source.Length;
        tagged[offset++] = DestinationStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            tagged.AsSpan(offset),
            checked((ushort)destination.Length));
        offset += sizeof(ushort);
        destination.CopyTo(tagged.AsSpan(offset));
        return SHA256.HashData(tagged);
    }

    private static bool IsValidCommand(
        KitBagItemMoveCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        AreValidDistinctSlots(
            command.SourceKitBagSlot,
            command.DestinationKitBagSlot) &&
        TryGetStateBytes(
            command.ExpectedSourceCompactItemState,
            out _) &&
        TryGetStateBytes(
            command.ExpectedDestinationCompactItemState,
            out _);

    private static bool AreValidDistinctSlots(
        int source,
        int destination) =>
        source is >= MinimumKitBagSlot and <= MaximumKitBagSlot &&
        destination is >= MinimumKitBagSlot and <= MaximumKitBagSlot &&
        source != destination;

    private static bool IsTrustedTransport(
        CommandTransportKind transport) =>
        transport is
            CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    private static bool TryGetStateBytes(
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
            return bytes.Length <= MaximumExpectedStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
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
