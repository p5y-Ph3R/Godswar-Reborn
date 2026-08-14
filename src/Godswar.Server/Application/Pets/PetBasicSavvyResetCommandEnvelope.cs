using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetBasicSavvyResetCommandEnvelope
{
    public static CommandEnvelope<PetBasicSavvyResetCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetBasicSavvyResetCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelope<PetBasicSavvyResetCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetBasicSavvyResetCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAtUtc, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetBasicSavvyResetCommand> envelope)
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
            CommandFamily.PetBasicSavvyReset,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetDurableCommandContract.CanonicalPetBasicSavvyReset(
                envelope.Command.Operation,
                envelope.Command.PreviewOperationId));
    }

    private static CommandEnvelope<PetBasicSavvyResetCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAtUtc,
        PetBasicSavvyResetCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetBasicSavvyReset,
            command.Identity.Strength,
            subject,
            connection,
            receivedAtUtc,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetDurableCommandContract.CanonicalPetBasicSavvyReset(
                command.Operation,
                command.PreviewOperationId),
            command);

    private static bool IsValidOperation(
        PetBasicSavvyResetCommand command) =>
        command.Operation switch
        {
            PetBasicSavvyResetOperation.Preview =>
                command.PreviewOperationId == Guid.Empty,
            // An OK can arrive after its local preview binding expired or
            // after reconnect. Preserve that exact intent so persistence can
            // return PreviewUnavailable instead of throwing before the
            // durable command boundary.
            PetBasicSavvyResetOperation.Accept => true,
            _ => false
        };
}
