using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class PetManagerUtilityCommandEnvelope
{
    public static CommandEnvelope<PetManagerUtilityCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetManagerUtilityCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetManagerUtilityCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetManagerUtilityCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetManagerUtilityCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            !Enum.IsDefined(envelope.Command.Operation) ||
            !HasValidSlot(envelope.Command))
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
            CommandFamily.PetManagerUtility,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetManagerUtilityCommandContract.CanonicalRequest(
                envelope.Command.Operation,
                envelope.Command.KitBagSlot));
    }

    private static bool HasValidSlot(PetManagerUtilityCommand command) =>
        command.Operation == PetManagerUtilityOperation.Unseal
            ? command.KitBagSlot is >=
                PetDurableCommandContract.MinimumKitBagSlot and <=
                PetDurableCommandContract.MaximumKitBagSlot
            : command.KitBagSlot == -1;

    private static CommandEnvelope<PetManagerUtilityCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetManagerUtilityCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetManagerUtility,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetManagerUtilityCommandContract.CanonicalRequest(
                command.Operation,
                command.KitBagSlot),
            command);
}
