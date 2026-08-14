using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetRebirthCommandEnvelope
{
    public static CommandEnvelope<PetRebirthCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetRebirthCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetRebirthCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetRebirthCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetRebirthCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            !PetRebirthMaterialContract.IsCanonicalSelection(
                envelope.Command.MaterialTemplateId,
                envelope.Command.Quantity))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!PetDurableCommandContract.HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.PetRebirth,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetRebirthCommandContract.CanonicalRequest(
                envelope.Command.MaterialTemplateId,
                envelope.Command.Quantity));
    }

    private static CommandEnvelope<PetRebirthCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetRebirthCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetRebirth,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetRebirthCommandContract.CanonicalRequest(
                command.MaterialTemplateId,
                command.Quantity),
            command);
}
