using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetOwnerMergeToggleCommandEnvelope
{
    public static CommandEnvelope<PetOwnerMergeToggleCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetOwnerMergeToggleCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelope<PetOwnerMergeToggleCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetOwnerMergeToggleCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetOwnerMergeToggleCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            !PetDurableCommandContract.HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.PetOwnerMergeToggle,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetDurableCommandContract.CanonicalPetOwnerMergeToggle());
    }

    private static CommandEnvelope<PetOwnerMergeToggleCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetOwnerMergeToggleCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetOwnerMergeToggle,
            command.Identity.Strength,
            subject,
            connection,
            receivedAtUtc,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetDurableCommandContract.CanonicalPetOwnerMergeToggle(),
            command);
}
