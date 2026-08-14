using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetSoulContractCommandEnvelope
{
    public static CommandEnvelope<PetSoulContractCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetSoulContractCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetSoulContractCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetSoulContractCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetSoulContractCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            envelope.Command.MaterialTemplateId !=
                PetSoulContractRules.ContractSpiritItemId ||
            envelope.Command.Quantity is < 0 or >
                PetSoulContractRules.MaximumSpiritCount)
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
            CommandFamily.PetSoulContract,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetSoulContractCommandContract.CanonicalRequest(
                envelope.Command.MaterialTemplateId,
                envelope.Command.Quantity));
    }

    private static CommandEnvelope<PetSoulContractCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetSoulContractCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetSoulContract,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetSoulContractCommandContract.CanonicalRequest(
                command.MaterialTemplateId,
                command.Quantity),
            command);
}
