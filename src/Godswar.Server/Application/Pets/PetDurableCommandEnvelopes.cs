using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class BagItemActivationCommandEnvelope
{
    public static CommandEnvelope<BagItemActivationCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        BagItemActivationCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.BagItemActivation,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(
                command.ClientOperationId),
            PetDurableCommandContract.CanonicalBagActivation(
                command.KitBagSlot),
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<BagItemActivationCommand> envelope)
    {
        if (envelope.Command.ClientOperationId == Guid.Empty ||
            envelope.Command.KitBagSlot is <
                PetDurableCommandContract.MinimumKitBagSlot or >
                PetDurableCommandContract.MaximumKitBagSlot ||
            !Enum.IsDefined(
                envelope.Command.ExecutionConstraint) ||
            !PetDurableCommandContract.IsTrusted(
                envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.BagItemActivation,
            CommandIdentityStrength.ClientOperationId,
            PetDurableCommandContract.OperationScope(
                envelope.Command.ClientOperationId),
            PetDurableCommandContract.CanonicalBagActivation(
                envelope.Command.KitBagSlot));
    }
}

internal static class PetLevelUpgradeCommandEnvelope
{
    public static CommandEnvelope<PetLevelUpgradeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetLevelUpgradeCommand command) =>
        CreateCore(
            CommandFamily.PetLevelUpgrade,
            subject,
            connection,
            receivedAt,
            command.ClientOperationId,
            command.PetId,
            operation: 0,
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetLevelUpgradeCommand> envelope) =>
        ValidateCore(
            envelope,
            CommandFamily.PetLevelUpgrade,
            envelope.Command.ClientOperationId,
            envelope.Command.PetId,
            operation: 0);

    private static CommandEnvelope<T> CreateCore<T>(
        CommandFamily family,
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        Guid operationId,
        long petId,
        byte operation,
        T command) =>
        CommandEnvelopeContract.Create(
            family,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(operationId),
            PetDurableCommandContract.CanonicalPet(petId, operation),
            command);

    internal static CommandEnvelopeValidation ValidateCore<T>(
        CommandEnvelope<T> envelope,
        CommandFamily family,
        Guid operationId,
        long petId,
        byte operation)
    {
        if (operationId == Guid.Empty ||
            petId is <= 0 or >
                PetDurableCommandContract.MaximumPetId ||
            !PetDurableCommandContract.IsTrusted(
                envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            family,
            CommandIdentityStrength.ClientOperationId,
            PetDurableCommandContract.OperationScope(operationId),
            PetDurableCommandContract.CanonicalPet(petId, operation));
    }
}

internal static class PetPresenceTransitionCommandEnvelope
{
    public static CommandEnvelope<PetPresenceTransitionCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetPresenceTransitionCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetPresenceTransition,
            CommandIdentityStrength.ClientOperationId,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(
                command.ClientOperationId),
            PetDurableCommandContract.CanonicalPet(
                command.PetId,
                checked((byte)((byte)command.Operation + 1))),
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetPresenceTransitionCommand> envelope)
    {
        if (!Enum.IsDefined(envelope.Command.Operation))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return PetLevelUpgradeCommandEnvelope.ValidateCore(
            envelope,
            CommandFamily.PetPresenceTransition,
            envelope.Command.ClientOperationId,
            envelope.Command.PetId,
            checked((byte)((byte)envelope.Command.Operation + 1)));
    }
}
