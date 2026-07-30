using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal readonly record struct ZodiacSkillGridSelectionCommand(
    Guid ClientOperationId,
    int GridIndex,
    int SelectedSkillKind);

internal static class ZodiacSkillGridSelectionCommandEnvelope
{
    public const int MinimumGridIndex = 0;
    public const int MaximumGridIndex = 15;
    public const int ClearSelection = -1;
    public const int MinimumSkillKind = 10_000;
    public const int MaximumSkillKind = 29_999;
    private const byte CanonicalVersion = 1;

    public static bool TryCreateCommand(
        Guid operationId,
        int gridIndex,
        int selectedSkillKind,
        out ZodiacSkillGridSelectionCommand command)
    {
        command = default;
        if (operationId == Guid.Empty ||
            gridIndex is < MinimumGridIndex or > MaximumGridIndex ||
            !IsValidIntentSkillKind(selectedSkillKind))
        {
            return false;
        }

        command = new(
            operationId,
            gridIndex,
            selectedSkillKind);
        return true;
    }

    public static CommandEnvelope<ZodiacSkillGridSelectionCommand>
        Create(
            CommandSubject subject,
            CommandConnectionCorrelation connection,
            DateTimeOffset receivedAt,
            ZodiacSkillGridSelectionCommand command)
    {
        if (!IsValidCommand(command) ||
            connection.Transport is not (
                CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        Span<byte> operationScope = stackalloc byte[16];
        command.ClientOperationId.TryWriteBytes(
            operationScope,
            bigEndian: true,
            out _);
        Span<byte> request = stackalloc byte[9];
        request[0] = CanonicalVersion;
        BinaryPrimitives.WriteInt32BigEndian(
            request.Slice(1, 4),
            command.GridIndex);
        BinaryPrimitives.WriteInt32BigEndian(
            request.Slice(5, 4),
            command.SelectedSkillKind);
        return CommandEnvelopeContract.Create(
            CommandFamily.ZodiacSkillGridSelection,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            operationScope,
            request,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!IsValidCommand(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        if (envelope.Connection.Transport is not (
                CommandTransportKind.SecureTlsLegacy or
                CommandTransportKind.SecureCommand))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        Span<byte> operationScope = stackalloc byte[16];
        envelope.Command.ClientOperationId.TryWriteBytes(
            operationScope,
            bigEndian: true,
            out _);
        Span<byte> request = stackalloc byte[9];
        request[0] = CanonicalVersion;
        BinaryPrimitives.WriteInt32BigEndian(
            request.Slice(1, 4),
            envelope.Command.GridIndex);
        BinaryPrimitives.WriteInt32BigEndian(
            request.Slice(5, 4),
            envelope.Command.SelectedSkillKind);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.ZodiacSkillGridSelection,
            CommandIdentityStrength.ClientOperationId,
            operationScope,
            request);
    }

    private static bool IsValidCommand(
        ZodiacSkillGridSelectionCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        command.GridIndex is
            >= MinimumGridIndex and <= MaximumGridIndex &&
        IsValidIntentSkillKind(command.SelectedSkillKind);

    private static bool IsValidIntentSkillKind(int skillKind) =>
        skillKind == ClearSelection ||
        skillKind is >= MinimumSkillKind and <= MaximumSkillKind;
}
