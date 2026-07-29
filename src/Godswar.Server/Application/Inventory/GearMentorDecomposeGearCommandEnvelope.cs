using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct GearMentorDecomposeSelection(
    int SelectedKitBagSlot,
    string ExpectedCompactItemState);

internal readonly record struct GearMentorDecomposeGearCommand(
    Guid ClientOperationId,
    int NpcId,
    ImmutableArray<GearMentorDecomposeSelection> Selections);

internal static class GearMentorDecomposeGearCommandEnvelope
{
    public const int SpartaGearMentorNpcId = 5067;
    public const int AthensGearMentorNpcId = 5209;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MinimumSelectionCount = 1;
    public const int MaximumSelectionCount = 3;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const int MaximumCombinedExpectedStateUtf8Bytes = 1_000;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int CanonicalPrefixBytes =
        sizeof(ushort) +
        sizeof(int) +
        sizeof(byte);
    private const int CanonicalSelectionPrefixBytes =
        sizeof(ushort) +
        sizeof(ushort);
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int npcId,
        IReadOnlyList<GearMentorDecomposeSelection>? selections,
        out GearMentorDecomposeGearCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty ||
            !IsPhysicalGearMentor(npcId) ||
            !TryCopySelections(selections, out var copy))
        {
            return false;
        }

        command = new GearMentorDecomposeGearCommand(
            clientOperationId,
            npcId,
            copy);
        return true;
    }

    public static CommandEnvelope<GearMentorDecomposeGearCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        GearMentorDecomposeGearCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Gear Mentor Decompose command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Gear Mentor Decompose requires authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        return CommandEnvelopeContract.Create(
            CommandFamily.GearMentorDecomposeGear,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            CreateCanonicalRequest(command),
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<GearMentorDecomposeGearCommand> envelope)
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
            CommandFamily.GearMentorDecomposeGear,
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
            CommandFamily.GearMentorDecomposeGear,
            subject,
            operationScope);
    }

    private static bool IsValidCommand(
        GearMentorDecomposeGearCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        IsPhysicalGearMentor(command.NpcId) &&
        AreSelectionsValid(command.Selections);

    private static bool TryCopySelections(
        IReadOnlyList<GearMentorDecomposeSelection>? selections,
        out ImmutableArray<GearMentorDecomposeSelection> copy)
    {
        copy = ImmutableArray<GearMentorDecomposeSelection>.Empty;
        if (selections is null ||
            selections.Count is
                < MinimumSelectionCount or > MaximumSelectionCount)
        {
            return false;
        }

        copy = ImmutableArray.CreateRange(selections);
        return AreSelectionsValid(copy);
    }

    private static bool AreSelectionsValid(
        ImmutableArray<GearMentorDecomposeSelection> selections)
    {
        if (selections.IsDefault ||
            selections.Length is
                < MinimumSelectionCount or > MaximumSelectionCount)
        {
            return false;
        }

        var totalStateBytes = 0;
        Span<bool> occupiedSlots = stackalloc bool[MaximumKitBagSlot + 1];
        occupiedSlots.Clear();
        foreach (var selection in selections)
        {
            if (selection.SelectedKitBagSlot is
                    < MinimumKitBagSlot or > MaximumKitBagSlot ||
                occupiedSlots[selection.SelectedKitBagSlot] ||
                !TryGetExpectedStateByteCount(
                    selection.ExpectedCompactItemState,
                    out var stateBytes))
            {
                return false;
            }

            occupiedSlots[selection.SelectedKitBagSlot] = true;
            totalStateBytes += stateBytes;
        }

        return totalStateBytes <= MaximumCombinedExpectedStateUtf8Bytes &&
            CanonicalPrefixBytes +
                (selections.Length * CanonicalSelectionPrefixBytes) +
                totalStateBytes <=
            CommandEnvelopeContract.MaximumCanonicalRequestBytes;
    }

    private static bool IsPhysicalGearMentor(int npcId) =>
        npcId is AthensGearMentorNpcId or SpartaGearMentorNpcId;

    private static bool IsTrustedTransport(
        CommandTransportKind transport) =>
        transport is
            CommandTransportKind.SecureTlsLegacy or
            CommandTransportKind.SecureCommand;

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

    private static byte[] CreateCanonicalRequest(
        GearMentorDecomposeGearCommand command)
    {
        var stateBytes = command.Selections
            .Select(static selection =>
                StrictUtf8.GetBytes(
                    selection.ExpectedCompactItemState))
            .ToArray();
        var length =
            CanonicalPrefixBytes +
            (command.Selections.Length *
                CanonicalSelectionPrefixBytes) +
            stateBytes.Sum(static value => value.Length);
        var canonical = new byte[length];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(ushort)..],
            command.NpcId);
        destination[sizeof(ushort) + sizeof(int)] =
            checked((byte)command.Selections.Length);

        var offset = CanonicalPrefixBytes;
        for (var index = 0; index < command.Selections.Length; index++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[offset..],
                checked((ushort)command.Selections[index]
                    .SelectedKitBagSlot));
            offset += sizeof(ushort);
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[offset..],
                checked((ushort)stateBytes[index].Length));
            offset += sizeof(ushort);
            stateBytes[index].CopyTo(destination[offset..]);
            offset += stateBytes[index].Length;
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
                "The operation ID could not be encoded.",
                nameof(clientOperationId));
        }
    }
}
