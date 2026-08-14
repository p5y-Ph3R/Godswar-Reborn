using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal readonly record struct PetAppearanceChangeCommand(
    PetCommandOperationIdentity Identity,
    int KitBagSlot)
{
    public Guid ClientOperationId =>
        Identity.IsSecureClient ? Identity.OperationId : Guid.Empty;
}

internal static class PetAppearanceChangeCommandEnvelope
{
    public static CommandEnvelope<PetAppearanceChangeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetAppearanceChangeCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetAppearanceChangeCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetAppearanceChangeCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetAppearanceChangeCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            envelope.Command.KitBagSlot is <
                PetDurableCommandContract.MinimumKitBagSlot or >
                PetDurableCommandContract.MaximumKitBagSlot)
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
            CommandFamily.PetAppearanceChange,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetDurableCommandContract.CanonicalBagActivation(
                envelope.Command.KitBagSlot));
    }

    private static CommandEnvelope<PetAppearanceChangeCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetAppearanceChangeCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetAppearanceChange,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(command.Identity),
            PetDurableCommandContract.CanonicalBagActivation(
                command.KitBagSlot),
            command);
}
