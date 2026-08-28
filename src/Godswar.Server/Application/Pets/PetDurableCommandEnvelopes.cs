using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal static class BagItemActivationCommandEnvelope
{
    public static CommandEnvelope<BagItemActivationCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        BagItemActivationCommand command)
    {
        RequireSecureProvenance(command.Identity, connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<BagItemActivationCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        BagItemActivationCommand command)
    {
        RequireRawLocalProvenance(command.Identity, connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<BagItemActivationCommand>
        CreateServerSessionLifecycle(
            CommandSubject subject,
            CommandConnectionCorrelation connection,
            DateTimeOffset receivedAt,
            BagItemActivationCommand command)
    {
        if (!command.Identity.IsServerSessionLifecycle ||
            !PetDurableCommandContract.HasMatchingProvenance(
                command.Identity,
                connection))
        {
            throw new ArgumentException(
                "Server bag-item lifecycle commands require the owning " +
                "session correlation.");
        }

        return CreateCore(subject, connection, receivedAt, command);
    }

    private static CommandEnvelope<BagItemActivationCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        BagItemActivationCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.BagItemActivation,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(
                command.Identity),
            PetDurableCommandContract.CanonicalBagActivation(
                command.KitBagSlot,
                command.Capture),
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<BagItemActivationCommand> envelope)
    {
        if (!PetDurableCommandContract.IsValidIdentity(
                envelope.Command.Identity) ||
            envelope.Command.KitBagSlot is <
                PetDurableCommandContract.MinimumKitBagSlot or >
                PetDurableCommandContract.MaximumKitBagSlot ||
            !Enum.IsDefined(
                envelope.Command.ExecutionConstraint) ||
            envelope.Command.Capture is { IsValid: false })
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
            CommandFamily.BagItemActivation,
            envelope.Command.Identity.Strength,
            PetDurableCommandContract.OperationScope(
                envelope.Command.Identity),
            PetDurableCommandContract.CanonicalBagActivation(
                envelope.Command.KitBagSlot,
                envelope.Command.Capture));
    }

    private static void RequireSecureProvenance(
        PetCommandOperationIdentity identity,
        CommandConnectionCorrelation connection)
    {
        if (!identity.IsSecureClient ||
            !PetDurableCommandContract.HasMatchingProvenance(
                identity,
                connection))
        {
            throw new ArgumentException(
                "Secure pet commands require a client operation identity " +
                "on a secure transport.");
        }
    }

    private static void RequireRawLocalProvenance(
        PetCommandOperationIdentity identity,
        CommandConnectionCorrelation connection)
    {
        if (!identity.IsRawLocalServer ||
            !PetDurableCommandContract.HasMatchingProvenance(
                identity,
                connection))
        {
            throw new ArgumentException(
                "Raw-local pet commands require a server operation " +
                "identity scoped to the exact legacy connection.");
        }
    }
}

internal static class PetLevelUpgradeCommandEnvelope
{
    public static CommandEnvelope<PetLevelUpgradeCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetLevelUpgradeCommand command)
    {
        RequireSecureProvenance(command.Identity, connection);
        return CreateCore(
            CommandFamily.PetLevelUpgrade,
            subject,
            connection,
            receivedAt,
            command.Identity,
            command.PetId,
            operation: 0,
            command);
    }

    public static CommandEnvelope<PetLevelUpgradeCommand> CreateRawLocal(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetLevelUpgradeCommand command)
    {
        RequireRawLocalProvenance(command.Identity, connection);
        return CreateCore(
            CommandFamily.PetLevelUpgrade,
            subject,
            connection,
            receivedAt,
            command.Identity,
            command.PetId,
            operation: 0,
            command);
    }

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<PetLevelUpgradeCommand> envelope) =>
        ValidateCore(
            envelope,
            CommandFamily.PetLevelUpgrade,
            envelope.Command.Identity,
            envelope.Command.PetId,
            operation: 0);

    private static CommandEnvelope<T> CreateCore<T>(
        CommandFamily family,
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetCommandOperationIdentity identity,
        long petId,
        byte operation,
        T command) =>
        CommandEnvelopeContract.Create(
            family,
            identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(identity),
            PetDurableCommandContract.CanonicalPet(petId, operation),
            command);

    internal static CommandEnvelopeValidation ValidateCore<T>(
        CommandEnvelope<T> envelope,
        CommandFamily family,
        PetCommandOperationIdentity identity,
        long petId,
        byte operation)
    {
        if (!PetDurableCommandContract.IsValidIdentity(identity) ||
            petId is <= 0 or >
                PetDurableCommandContract.MaximumPetId)
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }
        if (!PetDurableCommandContract.HasMatchingProvenance(
                identity,
                envelope.Connection))
        {
            return CommandEnvelopeValidation.InvalidCorrelation;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            family,
            identity.Strength,
            PetDurableCommandContract.OperationScope(identity),
            PetDurableCommandContract.CanonicalPet(petId, operation));
    }

    internal static void RequireSecureProvenance(
        PetCommandOperationIdentity identity,
        CommandConnectionCorrelation connection)
    {
        if (!identity.IsSecureClient ||
            !PetDurableCommandContract.HasMatchingProvenance(
                identity,
                connection))
        {
            throw new ArgumentException(
                "Secure pet commands require a client operation identity " +
                "on a secure transport.");
        }
    }

    internal static void RequireRawLocalProvenance(
        PetCommandOperationIdentity identity,
        CommandConnectionCorrelation connection)
    {
        if (!identity.IsRawLocalServer ||
            !PetDurableCommandContract.HasMatchingProvenance(
                identity,
                connection))
        {
            throw new ArgumentException(
                "Raw-local pet commands require a server operation " +
                "identity scoped to the exact legacy connection.");
        }
    }
}

internal static class PetPresenceTransitionCommandEnvelope
{
    public static CommandEnvelope<PetPresenceTransitionCommand> Create(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetPresenceTransitionCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireSecureProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetPresenceTransitionCommand>
        CreateRawLocal(
            CommandSubject subject,
            CommandConnectionCorrelation connection,
            DateTimeOffset receivedAt,
            PetPresenceTransitionCommand command)
    {
        PetLevelUpgradeCommandEnvelope.RequireRawLocalProvenance(
            command.Identity,
            connection);
        return CreateCore(subject, connection, receivedAt, command);
    }

    public static CommandEnvelope<PetPresenceTransitionCommand>
        CreateServerSessionLifecycle(
            CommandSubject subject,
            CommandConnectionCorrelation connection,
            DateTimeOffset receivedAt,
            PetPresenceTransitionCommand command)
    {
        if (!command.Identity.IsServerSessionLifecycle ||
            !PetDurableCommandContract.HasMatchingProvenance(
                command.Identity,
                connection))
        {
            throw new ArgumentException(
                "Server pet-presence lifecycle commands require the " +
                "owning session correlation.");
        }

        return CreateCore(subject, connection, receivedAt, command);
    }

    private static CommandEnvelope<PetPresenceTransitionCommand> CreateCore(
        CommandSubject subject,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        PetPresenceTransitionCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.PetPresenceTransition,
            command.Identity.Strength,
            subject,
            connection,
            receivedAt,
            PetDurableCommandContract.OperationScope(
                command.Identity),
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
            envelope.Command.Identity,
            envelope.Command.PetId,
            checked((byte)((byte)envelope.Command.Operation + 1)));
    }
}
