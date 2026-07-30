using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Characters;

internal static class CharacterCreateCommandEnvelope
{
    public static CommandEnvelope<CharacterCreateCommand> Create(
        int accountId,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        CharacterCreateCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.CharacterCreate,
            CommandIdentityStrength.ClientOperationId,
            new CommandSubject(accountId, 0),
            connection,
            receivedAt,
            CharacterLifecycleCommandContract.OperationScope(
                command.ClientOperationId),
            CharacterLifecycleCommandContract.CanonicalCreate(command),
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<CharacterCreateCommand> envelope)
    {
        if (!IsValid(envelope.Command) ||
            !CharacterLifecycleCommandContract.IsTrustedTransport(
                envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.CharacterCreate,
            CommandIdentityStrength.ClientOperationId,
            CharacterLifecycleCommandContract.OperationScope(
                envelope.Command.ClientOperationId),
            CharacterLifecycleCommandContract.CanonicalCreate(
                envelope.Command));
    }

    private static bool IsValid(CharacterCreateCommand command) =>
        command.ClientOperationId != Guid.Empty &&
        command.CharacterSlot ==
            CharacterLifecycleCommandContract.SingleCharacterSlot &&
        CharacterLifecycleCommandContract.IsValidName(command.Name) &&
        command.Gender <= 1 &&
        command.Camp <= 1 &&
        command.Profession <= 3 &&
        command.ZodiacType <= 11 &&
        command.Faith <= 3;
}

internal static class CharacterDeleteCommandEnvelope
{
    public static CommandEnvelope<CharacterDeleteCommand> Create(
        int accountId,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        CharacterDeleteCommand command) =>
        CommandEnvelopeContract.Create(
            CommandFamily.CharacterDelete,
            CommandIdentityStrength.ClientOperationId,
            new CommandSubject(accountId, 0),
            connection,
            receivedAt,
            CharacterLifecycleCommandContract.OperationScope(
                command.ClientOperationId),
            CharacterLifecycleCommandContract.CanonicalDelete(command),
            command);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<CharacterDeleteCommand> envelope)
    {
        if (!IsValid(envelope.Command) ||
            !CharacterLifecycleCommandContract.IsTrustedTransport(
                envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            CommandFamily.CharacterDelete,
            CommandIdentityStrength.ClientOperationId,
            CharacterLifecycleCommandContract.OperationScope(
                envelope.Command.ClientOperationId),
            CharacterLifecycleCommandContract.CanonicalDelete(
                envelope.Command));
    }

    private static bool IsValid(CharacterDeleteCommand command)
    {
        var expectedBoth =
            command.ExpectedActiveCharacterId.HasValue ==
            command.ExpectedLifecycleVersion.HasValue;
        return command.ClientOperationId != Guid.Empty &&
            command.CharacterSlot ==
                CharacterLifecycleCommandContract.SingleCharacterSlot &&
            CharacterLifecycleCommandContract.IsValidName(command.Name) &&
            expectedBoth &&
            (!command.ExpectedActiveCharacterId.HasValue ||
             command.ExpectedActiveCharacterId > 0 &&
             command.ExpectedLifecycleVersion > 0);
    }
}

internal static class CharacterRestoreCommandEnvelope
{
    public static CommandEnvelope<CharacterRestoreCommand> Create(
        int accountId,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        CharacterRestoreCommand command) =>
        CharacterLifecycleTargetEnvelope.Create(
            CommandFamily.CharacterRestore,
            accountId,
            connection,
            receivedAt,
            command,
            command.ClientOperationId,
            command.CharacterSlot,
            command.CharacterId,
            command.ExpectedLifecycleVersion);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<CharacterRestoreCommand> envelope) =>
        CharacterLifecycleTargetEnvelope.Validate(
            envelope,
            CommandFamily.CharacterRestore,
            envelope.Command.ClientOperationId,
            envelope.Command.CharacterSlot,
            envelope.Command.CharacterId,
            envelope.Command.ExpectedLifecycleVersion);
}

internal static class CharacterPurgeCommandEnvelope
{
    public static CommandEnvelope<CharacterPurgeCommand> Create(
        int accountId,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        CharacterPurgeCommand command) =>
        CharacterLifecycleTargetEnvelope.Create(
            CommandFamily.CharacterPurge,
            accountId,
            connection,
            receivedAt,
            command,
            command.ClientOperationId,
            command.CharacterSlot,
            command.CharacterId,
            command.ExpectedLifecycleVersion);

    public static CommandEnvelopeValidation Validate(
        CommandEnvelope<CharacterPurgeCommand> envelope) =>
        CharacterLifecycleTargetEnvelope.Validate(
            envelope,
            CommandFamily.CharacterPurge,
            envelope.Command.ClientOperationId,
            envelope.Command.CharacterSlot,
            envelope.Command.CharacterId,
            envelope.Command.ExpectedLifecycleVersion);
}

internal static class CharacterLifecycleTargetEnvelope
{
    public static CommandEnvelope<T> Create<T>(
        CommandFamily family,
        int accountId,
        CommandConnectionCorrelation connection,
        DateTimeOffset receivedAt,
        T command,
        Guid operationId,
        short slot,
        int characterId,
        long expectedVersion) =>
        CommandEnvelopeContract.Create(
            family,
            CommandIdentityStrength.ClientOperationId,
            new CommandSubject(accountId, 0),
            connection,
            receivedAt,
            CharacterLifecycleCommandContract.OperationScope(operationId),
            CharacterLifecycleCommandContract.CanonicalTarget(
                slot,
                characterId,
                expectedVersion),
            command);

    public static CommandEnvelopeValidation Validate<T>(
        CommandEnvelope<T> envelope,
        CommandFamily family,
        Guid operationId,
        short slot,
        int characterId,
        long expectedVersion)
    {
        if (operationId == Guid.Empty ||
            slot != CharacterLifecycleCommandContract.SingleCharacterSlot ||
            characterId <= 0 ||
            expectedVersion <= 0 ||
            !CharacterLifecycleCommandContract.IsTrustedTransport(
                envelope.Connection.Transport))
        {
            return CommandEnvelopeValidation.InvalidCommand;
        }

        return CommandEnvelopeContract.Validate(
            envelope,
            family,
            CommandIdentityStrength.ClientOperationId,
            CharacterLifecycleCommandContract.OperationScope(operationId),
            CharacterLifecycleCommandContract.CanonicalTarget(
                slot,
                characterId,
                expectedVersion));
    }
}
