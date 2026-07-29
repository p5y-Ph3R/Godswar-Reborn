using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum GearEnhancementCommandOperation : byte
{
    Enhance = 2,
    Add = 3,
    Delete = 6
}

internal enum GearEnhancementCommandItemRole : byte
{
    Gear = 1,
    Catalyst = 2,
    AttributeStone = 3
}

internal readonly record struct GearEnhancementCommandSelection(
    GearEnhancementCommandItemRole Role,
    int KitBagSlot,
    string ExpectedCompactItemState);

internal readonly record struct GearEnhancementCommand(
    Guid ClientOperationId,
    GearEnhancementCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    GearEnhancementCommandSelection Gear,
    GearEnhancementCommandSelection Catalyst,
    GearEnhancementCommandSelection AttributeStone);

internal static class GearEnhancementCommandEnvelope
{
    public const int SpartaGearMentorNpcId = 5067;
    public const int AthensGearMentorNpcId = 5209;
    public const int SpartaOriginEnhancerNpcId = 5140;
    public const int AthensOriginEnhancerNpcId = 5282;
    public const int GearMentorDialogIndex = 4;
    public const int OriginEnhancerDialogIndex = 118;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const int MaximumCombinedExpectedStateUtf8Bytes = 960;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int CanonicalPrefixBytes =
        sizeof(ushort) + OperationScopeBytes + sizeof(byte) +
        sizeof(int) + sizeof(int);
    private const int CanonicalSelectionPrefixBytes =
        sizeof(byte) + sizeof(ushort) + sizeof(ushort);
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        GearEnhancementCommandOperation operation,
        int npcId,
        int dialogIndex,
        GearEnhancementCommandSelection gear,
        GearEnhancementCommandSelection catalyst,
        GearEnhancementCommandSelection attributeStone,
        out GearEnhancementCommand command)
    {
        command = new GearEnhancementCommand(
            clientOperationId,
            operation,
            npcId,
            dialogIndex,
            gear,
            catalyst,
            attributeStone);
        if (IsValidCommand(command))
        {
            return true;
        }

        command = default;
        return false;
    }

    public static CommandEnvelope<GearEnhancementCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        GearEnhancementCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Gear Enhancement command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Gear Enhancement requires authenticated secure command " +
                "provenance.",
                nameof(connection));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
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
        CommandEnvelope<GearEnhancementCommand> envelope)
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
            Family(envelope.Command.Operation),
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            CreateCanonicalRequest(envelope.Command));
    }

    public static string CreateOperationId(
        CommandSubject subject,
        GearEnhancementCommandOperation operation,
        Guid clientOperationId)
    {
        if (!Enum.IsDefined(operation) ||
            clientOperationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A supported operation and non-empty operation UUID are " +
                "required.");
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            Family(operation),
            subject,
            operationScope);
    }

    public static CommandFamily Family(
        GearEnhancementCommandOperation operation) =>
        operation switch
        {
            GearEnhancementCommandOperation.Enhance =>
                CommandFamily.GearMentorEnhanceAttribute,
            GearEnhancementCommandOperation.Add =>
                CommandFamily.GearMentorAddAttribute,
            GearEnhancementCommandOperation.Delete =>
                CommandFamily.GearMentorDeleteAttribute,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    public static bool IsEndpoint(int npcId, int dialogIndex) =>
        (npcId, dialogIndex) is
            (SpartaGearMentorNpcId, GearMentorDialogIndex) or
            (AthensGearMentorNpcId, GearMentorDialogIndex) or
            (SpartaOriginEnhancerNpcId, OriginEnhancerDialogIndex) or
            (AthensOriginEnhancerNpcId, OriginEnhancerDialogIndex);

    public static IReadOnlyList<GearEnhancementCommandSelection>
        OrderedSelections(GearEnhancementCommand command) =>
        [command.Gear, command.Catalyst, command.AttributeStone];

    private static bool IsValidCommand(
        GearEnhancementCommand command)
    {
        if (command.ClientOperationId == Guid.Empty ||
            !Enum.IsDefined(command.Operation) ||
            !IsEndpoint(command.NpcId, command.DialogIndex))
        {
            return false;
        }

        var selections = OrderedSelections(command);
        if (selections[0].Role != GearEnhancementCommandItemRole.Gear ||
            selections[1].Role !=
                GearEnhancementCommandItemRole.Catalyst ||
            selections[2].Role !=
                GearEnhancementCommandItemRole.AttributeStone ||
            selections.Select(static value => value.KitBagSlot)
                .Distinct()
                .Count() != selections.Count)
        {
            return false;
        }

        var stateBytes = 0;
        foreach (var selection in selections)
        {
            if (selection.KitBagSlot is
                    < MinimumKitBagSlot or > MaximumKitBagSlot ||
                !TryGetExpectedStateByteCount(
                    selection.ExpectedCompactItemState,
                    out var bytes))
            {
                return false;
            }

            stateBytes += bytes;
        }

        return stateBytes <= MaximumCombinedExpectedStateUtf8Bytes &&
            CanonicalPrefixBytes +
                (selections.Count * CanonicalSelectionPrefixBytes) +
                stateBytes <=
            CommandEnvelopeContract.MaximumCanonicalRequestBytes;
    }

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
        GearEnhancementCommand command)
    {
        var selections = OrderedSelections(command);
        var stateBytes = selections
            .Select(static selection =>
                StrictUtf8.GetBytes(
                    selection.ExpectedCompactItemState))
            .ToArray();
        var canonical = new byte[
            CanonicalPrefixBytes +
            (selections.Count * CanonicalSelectionPrefixBytes) +
            stateBytes.Sum(static value => value.Length)];
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
        destination[offset++] = (byte)command.Operation;
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            command.NpcId);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[offset..],
            command.DialogIndex);
        offset += sizeof(int);

        for (var index = 0; index < selections.Count; index++)
        {
            destination[offset++] = (byte)selections[index].Role;
            BinaryPrimitives.WriteUInt16BigEndian(
                destination[offset..],
                checked((ushort)selections[index].KitBagSlot));
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
                "The operation UUID could not be encoded.",
                nameof(clientOperationId));
        }
    }
}
