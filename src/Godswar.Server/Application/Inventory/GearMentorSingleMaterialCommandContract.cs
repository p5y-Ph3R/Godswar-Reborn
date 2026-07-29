using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal static class GearMentorSingleMaterialCommandContract
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

    public static bool IsValidCommand(
        Guid clientOperationId,
        int npcId,
        int selectedKitBagSlot,
        string? expectedCompactItemState) =>
        clientOperationId != Guid.Empty &&
        IsPhysicalGearMentor(npcId) &&
        selectedKitBagSlot is
            >= MinimumKitBagSlot and <= MaximumKitBagSlot &&
        TryGetExpectedStateByteCount(
            expectedCompactItemState,
            out _);

    public static CommandEnvelope<TCommand> Create<TCommand>(
        CommandFamily family,
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        Guid clientOperationId,
        int npcId,
        int selectedKitBagSlot,
        string expectedCompactItemState,
        TCommand command)
    {
        if (!IsSupportedFamily(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family));
        }
        if (!IsValidCommand(
                clientOperationId,
                npcId,
                selectedKitBagSlot,
                expectedCompactItemState))
        {
            throw new ArgumentException(
                "The Gear Mentor material command is invalid.",
                nameof(command));
        }
        if (!IsTrustedTransport(connection.Transport))
        {
            throw new ArgumentException(
                "Gear Mentor material commands require authenticated " +
                "secure command provenance.",
                nameof(connection));
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        var canonicalRequest = CreateCanonicalRequest(
            npcId,
            selectedKitBagSlot,
            expectedCompactItemState);
        return CommandEnvelopeContract.Create(
            family,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate<TCommand>(
        CommandEnvelope<TCommand> envelope,
        CommandFamily family,
        Guid clientOperationId,
        int npcId,
        int selectedKitBagSlot,
        string? expectedCompactItemState)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsSupportedFamily(family))
        {
            return CommandEnvelopeValidation.InvalidFamily;
        }
        if (!IsValidCommand(
                clientOperationId,
                npcId,
                selectedKitBagSlot,
                expectedCompactItemState))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!IsTrustedTransport(envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        Span<byte> operationScope =
            stackalloc byte[OperationScopeBytes];
        WriteOperationScope(clientOperationId, operationScope);
        var canonicalRequest = CreateCanonicalRequest(
            npcId,
            selectedKitBagSlot,
            expectedCompactItemState!);
        return CommandEnvelopeContract.Validate(
            envelope,
            family,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            canonicalRequest);
    }

    public static string CreateOperationId(
        CommandFamily family,
        CommandSubject subject,
        Guid clientOperationId)
    {
        if (!IsSupportedFamily(family))
        {
            throw new ArgumentOutOfRangeException(nameof(family));
        }
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
            family,
            subject,
            operationScope);
    }

    private static bool IsPhysicalGearMentor(int npcId) =>
        npcId is AthensGearMentorNpcId or SpartaGearMentorNpcId;

    private static bool IsSupportedFamily(CommandFamily family) =>
        family is
            CommandFamily.GearMentorTransformCrystal or
            CommandFamily.GearMentorCombineGemPieces;

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
        int npcId,
        int selectedKitBagSlot,
        string expectedCompactItemState)
    {
        var stateBytes =
            StrictUtf8.GetBytes(expectedCompactItemState);
        var canonical = new byte[
            CanonicalPrefixBytes + stateBytes.Length];
        var destination = canonical.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(
            destination,
            CanonicalRequestVersion);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(ushort)..],
            npcId);
        BinaryPrimitives.WriteUInt16BigEndian(
            destination[(sizeof(ushort) + sizeof(int))..],
            checked((ushort)selectedKitBagSlot));
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
