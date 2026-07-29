using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct EquipmentBagTransferCommand(
    Guid ClientOperationId,
    int EquipmentSlot,
    int KitBagSlot,
    string ExpectedEquipmentCompactItemState,
    string ExpectedKitBagCompactItemState,
    bool MountRuntimeBlocked);

internal static class EquipmentBagTransferCommandEnvelope
{
    public const int MinimumEquipmentSlot = 0;
    public const int MaximumEquipmentSlot = 20;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const byte EquipmentStateRole = 1;
    private const byte KitBagStateRole = 2;
    private const int StateDigestBytes = 32;
    private const int CanonicalRequestBytes =
        sizeof(ushort) + (sizeof(ushort) * 2) + sizeof(byte) +
        StateDigestBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int equipmentSlot,
        int kitBagSlot,
        string? expectedEquipmentCompactItemState,
        string? expectedKitBagCompactItemState,
        out EquipmentBagTransferCommand command) =>
        TryCreateCommand(
            clientOperationId,
            equipmentSlot,
            kitBagSlot,
            expectedEquipmentCompactItemState,
            expectedKitBagCompactItemState,
            mountRuntimeBlocked: false,
            out command);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int equipmentSlot,
        int kitBagSlot,
        string? expectedEquipmentCompactItemState,
        string? expectedKitBagCompactItemState,
        bool mountRuntimeBlocked,
        out EquipmentBagTransferCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty ||
            !AreValidSlots(equipmentSlot, kitBagSlot) ||
            !IsValidMountRuntimeObservation(
                equipmentSlot,
                mountRuntimeBlocked) ||
            !TryGetStateBytes(
                expectedEquipmentCompactItemState,
                out _) ||
            !TryGetStateBytes(expectedKitBagCompactItemState, out _))
        {
            return false;
        }

        command = new EquipmentBagTransferCommand(
            clientOperationId,
            equipmentSlot,
            kitBagSlot,
            expectedEquipmentCompactItemState!,
            expectedKitBagCompactItemState!,
            mountRuntimeBlocked);
        return true;
    }

    public static CommandEnvelope<EquipmentBagTransferCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        EquipmentBagTransferCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The equipment/bag transfer command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Equipment/bag transfer requires authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            CommandFamily.EquipmentBagTransfer,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<EquipmentBagTransferCommand> envelope)
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
            CommandFamily.EquipmentBagTransfer,
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
            CommandFamily.EquipmentBagTransfer,
            subject,
            operationScope);
    }

    internal static byte[] CreateCanonicalRequest(
        EquipmentBagTransferCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The equipment/bag transfer command is invalid.",
                nameof(command));
        }

        var equipment = StrictUtf8.GetBytes(
            command.ExpectedEquipmentCompactItemState);
        var kitBag = StrictUtf8.GetBytes(
            command.ExpectedKitBagCompactItemState);
        var stateDigest = ComputeStateDigest(equipment, kitBag);
        var canonical = new byte[CanonicalRequestBytes];
        BinaryPrimitives.WriteUInt16BigEndian(
            canonical,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteUInt16BigEndian(
            canonical.AsSpan(sizeof(ushort)),
            checked((ushort)command.EquipmentSlot));
        BinaryPrimitives.WriteUInt16BigEndian(
            canonical.AsSpan(sizeof(ushort) * 2),
            checked((ushort)command.KitBagSlot));
        canonical[sizeof(ushort) * 3] =
            command.MountRuntimeBlocked ? (byte)1 : (byte)0;
        stateDigest.CopyTo(
            canonical.AsSpan(
                (sizeof(ushort) * 3) + sizeof(byte)));
        return canonical;
    }

    private static byte[] ComputeStateDigest(
        byte[] equipment,
        byte[] kitBag)
    {
        var tagged = new byte[
            1 + sizeof(ushort) + equipment.Length +
            1 + sizeof(ushort) + kitBag.Length];
        var offset = 0;
        tagged[offset++] = EquipmentStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            tagged.AsSpan(offset),
            checked((ushort)equipment.Length));
        offset += sizeof(ushort);
        equipment.CopyTo(tagged.AsSpan(offset));
        offset += equipment.Length;
        tagged[offset++] = KitBagStateRole;
        BinaryPrimitives.WriteUInt16BigEndian(
            tagged.AsSpan(offset),
            checked((ushort)kitBag.Length));
        offset += sizeof(ushort);
        kitBag.CopyTo(tagged.AsSpan(offset));
        return SHA256.HashData(tagged);
    }

    private static bool IsValidCommand(
        EquipmentBagTransferCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        AreValidSlots(command.EquipmentSlot, command.KitBagSlot) &&
        IsValidMountRuntimeObservation(
            command.EquipmentSlot,
            command.MountRuntimeBlocked) &&
        TryGetStateBytes(
            command.ExpectedEquipmentCompactItemState,
            out _) &&
        TryGetStateBytes(command.ExpectedKitBagCompactItemState, out _);

    private static bool AreValidSlots(
        int equipmentSlot,
        int kitBagSlot) =>
        equipmentSlot is
            >= MinimumEquipmentSlot and <= MaximumEquipmentSlot &&
        kitBagSlot is >= MinimumKitBagSlot and <= MaximumKitBagSlot;

    private static bool IsValidMountRuntimeObservation(
        int equipmentSlot,
        bool mountRuntimeBlocked) =>
        !mountRuntimeBlocked || equipmentSlot == MaximumEquipmentSlot;

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
