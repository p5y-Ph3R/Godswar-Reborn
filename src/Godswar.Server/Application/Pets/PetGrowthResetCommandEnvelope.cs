using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetGrowthResetCommandEnvelope
{
    public static CommandEnvelope<PetGrowthResetCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetGrowthResetCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelope<PetGrowthResetCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetGrowthResetCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetGrowthResetCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            !PetDurableCommandContract.HasMatchingProvenance(
                envelope.Command.Identity,
                envelope.Connection) ||
            !IsValidOperation(envelope.Command))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.PetGrowthReset,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetDurableCommandContract.CanonicalPetGrowthReset(
                envelope.Command.Operation,
                envelope.Command.PreviewOperationId));
    }

    private static CommandEnvelope<PetGrowthResetCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetGrowthResetCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetGrowthReset,
            command.Identity.Strength,
            subject,
            connection,
            receivedAtUtc,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetDurableCommandContract.CanonicalPetGrowthReset(
                command.Operation,
                command.PreviewOperationId),
            command);

    private static bool IsValidOperation(PetGrowthResetCommand command) =>
        command.Operation switch
        {
            PetGrowthResetOperation.Preview =>
                command.PreviewOperationId == Guid.Empty,
            PetGrowthResetOperation.Accept => true,
            _ => false
        };
}
