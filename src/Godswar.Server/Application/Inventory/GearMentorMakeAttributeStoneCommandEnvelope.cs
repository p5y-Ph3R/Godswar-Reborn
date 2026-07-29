using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct GearMentorMakeAttributeStoneCommand(
    Guid ClientOperationId,
    int NpcId,
    int SelectedKitBagSlot,
    string ExpectedCompactItemState);

internal static class GearMentorMakeAttributeStoneCommandEnvelope
{
    public const int SpartaGearMentorNpcId = 5067;
    public const int AthensGearMentorNpcId = 5209;
    public const int MinimumKitBagSlot = 0;
    public const int MaximumKitBagSlot = 95;
    public const int MaximumExpectedStateUtf8Bytes = 512;
    public const ushort CanonicalRequestVersion = 1;

    private const int OperationScopeBytes = 16;
    private const int CanonicalPrefixBytes =
        sizeof(ushort) +
        sizeof(int) +
        sizeof(ushort) +
        sizeof(ushort);
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    public static bool TryCreateCommand(
        Guid clientOperationId,
        int npcId,
        int selectedKitBagSlot,
        string? expectedCompactItemState,
        out GearMentorMakeAttributeStoneCommand command)
    {
        command = default;
        if (clientOperationId == Guid.Empty ||
            !IsPhysicalGearMentor(npcId) ||
            selectedKitBagSlot is
                < MinimumKitBagSlot or > MaximumKitBagSlot ||
            !TryGetExpectedStateByteCount(
                expectedCompactItemState,
                out _))
        {
            return false;
        }

        command = new GearMentorMakeAttributeStoneCommand(
            clientOperationId,
            npcId,
            selectedKitBagSlot,
            expectedCompactItemState!);
        return true;
    }

    public static CommandEnvelope<GearMentorMakeAttributeStoneCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        GearMentorMakeAttributeStoneCommand command)
    {
        if (!IsValidCommand(command))
        {
            throw new ArgumentException(
                "The Make Attribute Stone command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Make Attribute Stone requires authenticated secure " +
                "command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(command.ClientOperationId, operationScope);
        var canonicalRequest = CreateCanonicalRequest(command);
        return CommandEnvelopeContract.Create(
            CommandFamily.GearMentorMakeAttributeStone,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope)
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
        var canonicalRequest = CreateCanonicalRequest(envelope.Command);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.GearMentorMakeAttributeStone,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            canonicalRequest);
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

        Span<byte> operationScope = stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        return CommandEnvelopeContract.DeriveOperationId(
            CommandFamily.GearMentorMakeAttributeStone,
            subject,
            operationScope);
    }

    private static bool IsValidCommand(
        GearMentorMakeAttributeStoneCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        IsPhysicalGearMentor(command.NpcId) &&
        command.SelectedKitBagSlot is
            >= MinimumKitBagSlot and <= MaximumKitBagSlot &&
        TryGetExpectedStateByteCount(
            command.ExpectedCompactItemState,
            out _);

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
        if (string.IsNullOrEmpty(value) ||
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
        GearMentorMakeAttributeStoneCommand command)
    {
        var stateBytes =
            StrictUtf8.GetBytes(command.ExpectedCompactItemState);
        var canonical = new byte[
            CanonicalPrefixBytes + stateBytes.Length];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(ushort)..],
            command.NpcId);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[(sizeof(ushort) + sizeof(int))..],
            checked((ushort)command.SelectedKitBagSlot));
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[
                (sizeof(ushort) + sizeof(int) + sizeof(ushort))..],
            checked((ushort)stateBytes.Length));
        stateBytes.CopyTo(destination[CanonicalPrefixBytes..]);
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
