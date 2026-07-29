using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum EquipmentForgeCommandItemRole : byte
{
    Equipment = 1,
    PrimaryMaterial = 2,
    OddsMaterial = 3
}

internal readonly record struct EquipmentForgeCommandSelection(
    EquipmentForgeCommandItemRole Role,
    int KitBagSlot,
    int Quantity,
    string ExpectedCompactItemState);

internal readonly record struct EquipmentForgeCommand(
    Guid ClientOperationId,
    EquipmentForgeCommandSelection Equipment,
    EquipmentForgeCommandSelection PrimaryMaterial,
    ImmutableArray<EquipmentForgeCommandSelection> OddsMaterials);

internal static class EquipmentForgeCommandEnvelope
{
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumOddsQuantity = 25;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const int MaximumCombinedExpectedStateUtf8Bytes = 13_824;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int StateHashBytes = 32;
    private const int CanonicalPrefixBytes =
        sizeof(ushort) + OperationScopeBytes + sizeof(byte);
    private const int CanonicalSelectionBytes =
        sizeof(byte) + sizeof(byte) + sizeof(byte) + StateHashBytes;
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        EquipmentForgeCommandSelection equipment,
        EquipmentForgeCommandSelection primaryMaterial,
        IReadOnlyList<EquipmentForgeCommandSelection>? oddsMaterials,
        out EquipmentForgeCommand command)
    {
        command = new EquipmentForgeCommand(
            clientOperationId,
            equipment,
            primaryMaterial,
            oddsMaterials is null
                ? []
                : ImmutableArray.CreateRange(
                    oddsMaterials.OrderBy(static item => item.KitBagSlot)));
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<EquipmentForgeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        EquipmentForgeCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The equipment-forge command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Equipment forge requires authenticated secure command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            CommandFamily.EquipmentForge,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<EquipmentForgeCommand> envelope)
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

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(
            envelope.Command.ClientOperationId,
            operationScope);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.EquipmentForge,
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
                "A non-empty operation UUID is required.",
                nameof(clientOperationId));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            CommandFamily.EquipmentForge,
            subject,
            operationScope);
    }

    public static IReadOnlyList<EquipmentForgeCommandSelection>
        OrderedSelections(EquipmentForgeCommand command) =>
        [command.Equipment, command.PrimaryMaterial, .. command.OddsMaterials];

    private static bool IsValidCommand(EquipmentForgeCommand command)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.OddsMaterials.IsDefault ||
            command.OddsMaterials.Length > MaximumOddsQuantity)
        {
            return false;
        }

        var selections = OrderedSelections(command);
        if (command.Equipment.Role !=
                EquipmentForgeCommandItemRole.Equipment ||
            command.PrimaryMaterial.Role !=
                EquipmentForgeCommandItemRole.PrimaryMaterial ||
            command.Equipment.Quantity != 1 ||
            command.PrimaryMaterial.Quantity != 1 ||
            command.OddsMaterials.Any(static item =>
                item.Role != EquipmentForgeCommandItemRole.OddsMaterial) ||
            command.OddsMaterials.Select(static item => item.KitBagSlot)
                .SequenceEqual(
                    command.OddsMaterials
                        .Select(static item => item.KitBagSlot)
                        .Order()) is false ||
            selections.Select(static item => item.KitBagSlot)
                .Distinct()
                .Count() != selections.Count)
        {
            return false;
        }

        var totalOdds = 0;
        var totalStateBytes = 0;
        foreach (var selection in selections)
        {
            if (selection.KitBagSlot is
                    < MinimumKitBagSlot or > MaximumKitBagSlot ||
                selection.Quantity is < 1 or > MaximumOddsQuantity ||
                !TryGetExpectedStateByteCount(
                    selection.ExpectedCompactItemState,
                    out var stateBytes))
            {
                return false;
            }

            totalStateBytes += stateBytes;
            if (selection.Role == EquipmentForgeCommandItemRole.OddsMaterial)
            {
                totalOdds += selection.Quantity;
            }
        }

        return totalOdds <= MaximumOddsQuantity &&
            totalStateBytes <= MaximumCombinedExpectedStateUtf8Bytes &&
            CanonicalPrefixBytes +
                (selections.Count * CanonicalSelectionBytes) <=
            CommandEnvelopeContract.MaximumCanonicalRequestBytes;
    }

    private static bool TryGetExpectedStateByteCount(
        string? value,
        out int byteCount)
    {
        byteCount = 0;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
            return byteCount <= MaximumExpectedStateUtf8Bytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsTrustedTransport(CommandTransportKind transport) =>
        transport is CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

    private static byte[] CreateCanonicalRequest(
        EquipmentForgeCommand command)
    {
        var selections = OrderedSelections(command);
        var canonical = new byte[
            CanonicalPrefixBytes +
            (selections.Count * CanonicalSelectionBytes)];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        if (!command.ClientOperationId.TryWriteBytes(
                destination[sizeof(ushort)..],
                bigEndian: true,
                out var guidBytes) ||
            guidBytes != OperationScopeBytes)
        {
            throw new InvalidOperationException(
                "The operation UUID could not be encoded.");
        }

        var offset = sizeof(ushort) + OperationScopeBytes;
        destination[offset++] = checked((byte)selections.Count);
        foreach (var selection in selections)
        {
            destination[offset++] = (byte)selection.Role;
            destination[offset++] = checked((byte)selection.KitBagSlot);
            destination[offset++] = checked((byte)selection.Quantity);
            SHA256.HashData(
                StrictUtf8.GetBytes(selection.ExpectedCompactItemState),
                destination.Slice(offset, StateHashBytes));
            offset += StateHashBytes;
        }

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
                "The operation UUID could not be encoded.",
                nameof(clientOperationId));
        }
    }
}
