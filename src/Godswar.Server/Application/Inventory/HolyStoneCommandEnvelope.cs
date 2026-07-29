using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum HolyStoneCommandOperation : byte
{
    Mount = 1,
    Remove = 2,
    Drill = 3
}

internal enum HolyStoneTargetLocation : byte
{
    Equipment = 0,
    KitBag = 1
}

internal readonly record struct HolyStoneCommand(
    Guid ClientOperationId,
    HolyStoneCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    HolyStoneTargetLocation TargetLocation,
    int TargetSlot,
    string ExpectedTargetCompactItemState,
    int SocketIndex,
    int StoneKitBagSlot,
    string ExpectedStoneCompactItemState);

internal static class HolyStoneCommandEnvelope
{
    public const int SpartaNpcId = 5083;
    public const int AthensNpcId = 5225;
    public const int DialogIndex = 30;
    public const int WeaponEquipmentSlot = 10;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MinimumSocketIndex = 0;
    public const int MaximumSocketIndex = 3;
    public const int ServerSelectedSocketIndex = -1;
    public const int NoStoneKitBagSlot = -1;
    public const int MaximumCompactItemStateUtf8Bytes = 512;
    public const int MaximumCombinedStateUtf8Bytes = 900;
    public const ushort CanonicalRequestVersion = 2;

    private const int OperationScopeBytes = 16;
    private const int StateDigestBytes = 32;
    private const int CanonicalArtisanEndpoint = 1;
    private const byte TargetStateRole = 1;
    private const byte StoneStateRole = 2;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + sizeof(byte) + sizeof(int) + sizeof(int) +
        sizeof(byte) + sizeof(short) + sizeof(short) + sizeof(short) +
        StateDigestBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        string? expectedTargetCompactItemState,
        int socketIndex,
        int stoneKitBagSlot,
        string? expectedStoneCompactItemState,
        out HolyStoneCommand command)
    {
        command = new HolyStoneCommand(
            clientOperationId,
            operation,
            npcId,
            dialogIndex,
            targetLocation,
            targetSlot,
            expectedTargetCompactItemState ?? string.Empty,
            socketIndex,
            stoneKitBagSlot,
            expectedStoneCompactItemState ?? string.Empty);
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<HolyStoneCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        HolyStoneCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Holy Stone command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Holy Stone commands require authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            Family(command.Operation),
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<HolyStoneCommand> envelope)
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
            Family(envelope.Command.Operation),
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        HolyStoneCommandOperation operation,
        Guid clientOperationId)
    {
        if (!Enum.IsDefined(operation) ||
            clientOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A supported operation and non-empty UUID are required.");
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            Family(operation),
            subject,
            operationScope);
    }

    public static CommandFamily Family(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount =>
                CommandFamily.HolyStoneMount,
            HolyStoneCommandOperation.Remove =>
                CommandFamily.HolyStoneRemove,
            HolyStoneCommandOperation.Drill =>
                CommandFamily.HolyStoneDrill,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static bool IsEndpoint(int npcId, int dialogIndex) =>
        dialogIndex == DialogIndex &&
        npcId is SpartaNpcId or AthensNpcId;

    public static bool AreEquivalentEndpoints(
        int firstNpcId,
        int firstDialogIndex,
        int secondNpcId,
        int secondDialogIndex) =>
        IsEndpoint(firstNpcId, firstDialogIndex) &&
        IsEndpoint(secondNpcId, secondDialogIndex);

    private static bool IsValidCommand(HolyStoneCommand command)
    {
        if (command.ClientOperationId == Guid.Empty ||
            !Enum.IsDefined(command.Operation) ||
            !IsEndpoint(command.NpcId, command.DialogIndex) ||
            !Enum.IsDefined(command.TargetLocation) ||
            !IsValidTargetSlot(
                command.TargetLocation,
                command.TargetSlot) ||
            !TryGetStateBytes(
                command.ExpectedTargetCompactItemState,
                allowEmpty: true,
                out var targetStateBytes) ||
            !TryGetStateBytes(
                command.ExpectedStoneCompactItemState,
                allowEmpty: true,
                out var stoneStateBytes) ||
            targetStateBytes.Length + stoneStateBytes.Length >
                MaximumCombinedStateUtf8Bytes)
        {
            return false;
        }

        return command.Operation switch
        {
            HolyStoneCommandOperation.Mount =>
                command.SocketIndex == ServerSelectedSocketIndex &&
                IsKitBagSlot(command.StoneKitBagSlot) &&
                (command.TargetLocation !=
                    HolyStoneTargetLocation.KitBag ||
                 command.TargetSlot != command.StoneKitBagSlot),
            HolyStoneCommandOperation.Remove =>
                command.SocketIndex is
                    >= MinimumSocketIndex and <= MaximumSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]",
            HolyStoneCommandOperation.Drill =>
                command.SocketIndex == ServerSelectedSocketIndex &&
                command.StoneKitBagSlot == NoStoneKitBagSlot &&
                command.ExpectedStoneCompactItemState == "[]",
            _ => false
        };
    }

    private static bool IsValidTargetSlot(
        HolyStoneTargetLocation location,
        int slot) =>
        location switch
        {
            HolyStoneTargetLocation.Equipment =>
                slot == WeaponEquipmentSlot,
            HolyStoneTargetLocation.KitBag => IsKitBagSlot(slot),
            _ => false
        };

    private static bool IsKitBagSlot(int slot) =>
        slot is >= MinimumKitBagSlot and <= MaximumKitBagSlot;

    private static byte[] CreateCanonicalRequest(
        HolyStoneCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Holy Stone command is invalid.",
                nameof(command));
        }

        var targetState = StrictUtf8.GetBytes(
            command.ExpectedTargetCompactItemState);
        var stoneState = StrictUtf8.GetBytes(
            command.ExpectedStoneCompactItemState);
        var canonical = new byte[CanonicalRequestBytes];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        var offset = sizeof(ushort);
        destination[offset++] = (byte)command.Operation;
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            CanonicalArtisanEndpoint);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            DialogIndex);
        offset += sizeof(int);
        destination[offset++] = (byte)command.TargetLocation;
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.TargetSlot));
        offset += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.SocketIndex));
        offset += sizeof(short);
        BinaryPrimitives.WriteInt16BigEndian(
            destination[offset..],
            checked((short)command.StoneKitBagSlot));
        offset += sizeof(short);
        ComputeStateDigest(targetState, stoneState)
            .CopyTo(destination[offset..]);
        return canonical;
    }

    private static byte[] ComputeStateDigest(
        byte[] targetState,
        byte[] stoneState)
    {
        var tagged = new byte[
            sizeof(byte) + sizeof(ushort) + targetState.Length +
            sizeof(byte) + sizeof(ushort) + stoneState.Length];
        var destination = tagged.AsSpan();
        var offset = 0;
        destination[offset++] = TargetStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)targetState.Length));
        offset += sizeof(ushort);
        targetState.CopyTo(destination[offset..]);
        offset += targetState.Length;
        destination[offset++] = StoneStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[offset..],
            checked((ushort)stoneState.Length));
        offset += sizeof(ushort);
        stoneState.CopyTo(destination[offset..]);
        return SHA256.HashData(tagged);
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
            (!allowEmpty && value == "[]"))
        {
            return false;
        }

        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return bytes.Length <= MaximumCompactItemStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsTrustedTransport(
        CommandTransportKind transport) =>
        transport is
            CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

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
                "The operation UUID could not be encoded.",
                nameof(clientOperationId));
        }
    }
}
