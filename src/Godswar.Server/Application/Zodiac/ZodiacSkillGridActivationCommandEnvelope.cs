using System.Buffers.Binary;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal readonly record struct ZodiacSkillGridActivationCommand(
    int GridIndex,
    int ExpectedLevel);

internal static class ZodiacSkillGridActivationCommandEnvelope
{
    public const int MinimumGridIndex = 0;
    public const int MaximumGridIndex = 15;
    public const int ExpectedInactiveLevel = 0;
    public const byte ActivatedLevel = 1;
    public const byte MaximumGridLevel = 50;
    public const int NoSelectedSkillId = -1;

    public static bool TryCreateCommand(
        int gridIndex,
        int expectedLevel,
        out ZodiacSkillGridActivationCommand command)
    {
        command = default;
        if (gridIndex is < MinimumGridIndex or > MaximumGridIndex ||
            expectedLevel != ExpectedInactiveLevel)
        {
            return false;
        }

        command = new ZodiacSkillGridActivationCommand(
            gridIndex,
            expectedLevel);
        return true;
    }

    public static CommandEnvelope<ZodiacSkillGridActivationCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        ZodiacSkillGridActivationCommand command)
    {
        Span<byte> operationScope = stackalloc byte[sizeof(int) * 2];
        WriteIdentity(command, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[sizeof(int) * 2];
        WriteIdentity(command, canonicalRequest);

        return CommandEnvelopeContract.Create(
            CommandFamily.ZodiacSkillGridActivation,
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.ZodiacSkillGridActivation),
            subject,
            connection,
            receivedAt,
            operationScope,
            canonicalRequest,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<ZodiacSkillGridActivationCommand> envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!TryCreateCommand(
                envelope.Command.GridIndex,
                envelope.Command.ExpectedLevel,
                out _))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        Span<byte> operationScope = stackalloc byte[sizeof(int) * 2];
        WriteIdentity(envelope.Command, operationScope);
        Span<byte> canonicalRequest = stackalloc byte[sizeof(int) * 2];
        WriteIdentity(envelope.Command, canonicalRequest);
        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.ZodiacSkillGridActivation,
            LegacyCommandIdentityPolicy.GetIdentityStrength(
                CommandFamily.ZodiacSkillGridActivation),
            operationScope,
            canonicalRequest);
    }

    private static void WriteIdentity(
        ZodiacSkillGridActivationCommand command,
        Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32BigEndian(
            destination[..sizeof(int)],
            command.GridIndex);
        BinaryPrimitives.WriteInt32BigEndian(
            destination[sizeof(int)..],
            command.ExpectedLevel);
    }
}
