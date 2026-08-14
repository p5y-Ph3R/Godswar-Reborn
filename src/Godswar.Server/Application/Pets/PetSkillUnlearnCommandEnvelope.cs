using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetSkillUnlearnCommandEnvelope
{
    public const int MinimumSkillSlot = 0;
    public const int MaximumSkillSlot = 11;

    public static CommandEnvelope<PetSkillUnlearnCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetSkillUnlearnCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetSkillUnlearnCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetSkillUnlearnCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetSkillUnlearnCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            envelope.Command.SkillSlot is < MinimumSkillSlot or
                > MaximumSkillSlot ||
            !PetDurableCommandContract.HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.PetSkillUnlearn,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetDurableCommandContract.CanonicalSkillSlot(
                envelope.Command.SkillSlot));
    }

    private static CommandEnvelope<PetSkillUnlearnCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetSkillUnlearnCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetSkillUnlearn,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetDurableCommandContract.CanonicalSkillSlot(
                command.SkillSlot),
            command);
}
