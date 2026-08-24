using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Warehouse;

internal static class WarehouseTransferCommandEnvelope
{
    public const ushort CanonicalRequestVersion = 1;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    private const byte SourceStateRole = 1;
    private const byte DestinationStateRole = 2;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        WarehouseOperationIdentity identity,
        int realmId,
        WarehouseTransferOperation operation,
        int warehouseSlot,
        int kitBagSlot,
        int destinationWarehouseSlot,
        int money,
        WarehouseStorageType storageType,
        long expectedWarehouseRevision,
        long expectedInventoryRevision,
        string? expectedSourceCompactItemState,
        string? expectedDestinationCompactItemState,
        out WarehouseTransferCommand command)
    {
        command = new(
            identity,
            realmId,
            operation,
            warehouseSlot,
            kitBagSlot,
            destinationWarehouseSlot,
            money,
            storageType,
            expectedWarehouseRevision,
            expectedInventoryRevision,
            expectedSourceCompactItemState ?? string.Empty,
            expectedDestinationCompactItemState ?? string.Empty);
        if (IsValid(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<WarehouseTransferCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        WarehouseTransferCommand command)
    {
        if (!IsValid(command) ||
            !WarehouseCommandIdentityRules.Matches(
                command.Identity,
                connection))
        {
            throw new ArgumentException(
                "The warehouse transfer command is invalid.",
                nameof(command));
        }

        return CommandEnvelopeContract.Create(
            CommandFamily.WarehouseTransfer,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            WarehouseCommandIdentityRules.CreateScope(command.Identity),
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<WarehouseTransferCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValid(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!WarehouseCommandIdentityRules.Matches(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.WarehouseTransfer,
            envelope.Command.Identity.Strength,
            WarehouseCommandIdentityRules.CreateScope(
                envelope.Command.Identity),
            CreateCanonicalRequest(envelope.Command));
    }

    private static bool IsValid(WarehouseTransferCommand command)
    {
        if ((!command.Identity.IsSecureClient &&
             !command.Identity.IsRawLocalServer) ||
            !Enum.IsDefined(command.Operation) ||
            command.RealmId <= 0 ||
            command.Money != 0 ||
            command.StorageType != WarehouseStorageType.Normal ||
            command.ExpectedWarehouseRevision < 0 ||
            command.ExpectedInventoryRevision < 0 ||
            !TryGetStateBytes(
                command.ExpectedSourceCompactItemState,
                allowEmpty: false,
                out _) ||
            !TryGetStateBytes(
                command.ExpectedDestinationCompactItemState,
                allowEmpty: true,
                out _))
        {
            return false;
        }

        return command.Operation switch
        {
            WarehouseTransferOperation.Deposit =>
                (WarehouseCapacityPolicy.IsValidWarehouseSlot(
                     command.WarehouseSlot) ||
                 command.WarehouseSlot ==
                    WarehouseCapacityPolicy.AutomaticWarehouseSlot) &&
                WarehouseCapacityPolicy.IsValidKitBagSlot(
                    command.KitBagSlot) &&
                command.DestinationWarehouseSlot == -1,
            WarehouseTransferOperation.Withdraw =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    command.WarehouseSlot) &&
                (WarehouseCapacityPolicy.IsValidKitBagSlot(
                     command.KitBagSlot) ||
                 command.KitBagSlot ==
                    WarehouseCapacityPolicy.AutomaticKitBagSlot) &&
                command.DestinationWarehouseSlot == -1,
            WarehouseTransferOperation.InternalMove =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    command.WarehouseSlot) &&
                command.KitBagSlot ==
                    WarehouseCapacityPolicy.AutomaticKitBagSlot &&
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    command.DestinationWarehouseSlot) &&
                command.WarehouseSlot !=
                    command.DestinationWarehouseSlot,
            _ => false
        };
    }

    private static byte[] CreateCanonicalRequest(
        WarehouseTransferCommand command)
    {
        TryGetStateBytes(
            command.ExpectedSourceCompactItemState,
            allowEmpty: false,
            out var sourceState);
        TryGetStateBytes(
            command.ExpectedDestinationCompactItemState,
            allowEmpty: true,
            out var destinationState);
        var stateDigest = ComputeStateDigest(sourceState, destinationState);
        var bytes = new byte[
            sizeof(ushort) + sizeof(byte) + sizeof(int) * 5 +
            sizeof(ushort) + sizeof(long) * 2 + stateDigest.Length];
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes,
            CanonicalRequestVersion);
        bytes[2] = (byte)command.Operation;
        var offset = 3;
        WriteInt(command.RealmId);
        WriteInt(command.WarehouseSlot);
        WriteInt(command.KitBagSlot);
        WriteInt(command.DestinationWarehouseSlot);
        WriteInt(command.Money);
        BinaryPrimitives.WriteUInt16BigEndian(
            bytes.AsSpan(offset),
            (ushort)command.StorageType);
        offset += sizeof(ushort);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(offset),
            command.ExpectedWarehouseRevision);
        offset += sizeof(long);
        BinaryPrimitives.WriteInt64BigEndian(
            bytes.AsSpan(offset),
            command.ExpectedInventoryRevision);
        offset += sizeof(long);
        stateDigest.CopyTo(bytes.AsSpan(offset));
        return bytes;

        void WriteInt(int value)
        {
            BinaryPrimitives.WriteInt32BigEndian(
                bytes.AsSpan(offset, sizeof(int)),
                value);
            offset += sizeof(int);
        }
    }

    private static bool TryGetStateBytes(
        string? value,
        bool allowEmpty,
        out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']' ||
            !allowEmpty && value == "[]")
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

    private static byte[] ComputeStateDigest(
        byte[] source,
        byte[] destination)
    {
        var tagged = new byte[
            sizeof(byte) + sizeof(ushort) + source.Length +
            sizeof(byte) + sizeof(ushort) + destination.Length];
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
}

internal static class WarehouseCommandIdentityRules
{
    public static bool Matches(
        WarehouseOperationIdentity identity,
        CommandConnectionCorrelation connection) =>
        identity.IsSecureClient
            ? connection.Transport is
                CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand
            : identity.IsRawLocalServer &&
              connection.Transport == CommandTransportKind.LegacyTcp &&
              identity.RawLocalConnectionId == connection.ConnectionId;

    public static byte[] CreateScope(
        WarehouseOperationIdentity identity)
    {
        var bytes = new byte[33];
        bytes[0] = (byte)identity.Strength;
        WriteGuid(identity.OperationId, bytes.AsSpan(1, 16));
        WriteGuid(identity.RawLocalConnectionId, bytes.AsSpan(17, 16));
        return bytes;
    }

    private static void WriteGuid(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(
                destination,
                bigEndian: true,
                out var written) ||
            written != 16)
        {
            throw new ArgumentException(
                "The warehouse operation identity is invalid.");
        }
    }
}
