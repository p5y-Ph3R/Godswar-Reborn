using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Talents;

internal readonly record struct TalentUpgradeCommand(
    int TalentId,
    int ExpectedRank);

internal static class TalentUpgradeCommandEnvelope
{
    public const int MinimumTalentId = 0;
    public const int MaximumTalentId = 1_000_000;
    public const int MinimumExpectedRank = 0;
    public const int MaximumExpectedRank = 99;

    public static bool TryCreateCommand(
        int talentId,
        int expectedRank,
        out TalentUpgradeCommand command)
    {
        command = default;
        if (talentId is < MinimumTalentId or > MaximumTalentId ||
            expectedRank is < MinimumExpectedRank or > MaximumExpectedRank)
        {
            return false;
        }

        command = new TalentUpgradeCommand(
            talentId,
            expectedRank);
        return true;
    }

    public static CommandEnvelope<TalentUpgradeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        TalentUpgradeCommand command)
    {
        Span<byte> operationScope = stackalloc byte[sizeof(int) * 2];
        WriteOperationScope(command, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[sizeof(int) * 2];
        WriteCanonicalRequest(command, canonicalRequest);

        return CommandEnvelopeContract.Create(
            CommandFamily.TalentUpgrade,
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.TalentUpgrade),
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<TalentUpgradeCommand> envelope)
    {
        if (!TryCreateCommand(
                envelope.Command.TalentId,
                envelope.Command.ExpectedRank,
                out _))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        Span<byte> operationScope = stackalloc byte[sizeof(int) * 2];
        WriteOperationScope(envelope.Command, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[sizeof(int) * 2];
        WriteCanonicalRequest(envelope.Command, canonicalRequest);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.TalentUpgrade,
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.TalentUpgrade),
            operationScope,
            canonicalRequest);
    }

    private static void WriteOperationScope(
        TalentUpgradeCommand command,
        Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(
            destination[..sizeof(int)],
            command.TalentId);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(int)..],
            command.ExpectedRank);
    }

    private static void WriteCanonicalRequest(
        TalentUpgradeCommand command,
        Span<byte> destination)
    {
        WriteOperationScope(command, destination);
    }
}
