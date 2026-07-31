using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly ICharacterLifecycleCommandExecutor?
        _characterLifecycleCommands;

    private async Task HandleCharacterCreateRequestAsync(
        GamePacket packet,
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        if (packet.ClientOperationId is { } operationId)
        {
            await HandleDurableCharacterCreateAsync(
                operationId,
                character,
                cancellationToken);
            return;
        }

        if (_session.IsSecure)
        {
            await RejectMissingLifecycleIdentityAsync(
                CommandFamily.CharacterCreate,
                "create",
                cancellationToken);
            return;
        }

        if (_characterLifecycleCommands is not null)
        {
            await RejectMixedLifecycleProfileAsync(
                CommandFamily.CharacterCreate,
                "create",
                cancellationToken);
            return;
        }

        await HandleCompatibilityCharacterCreateAsync(
            character,
            cancellationToken);
    }

    private async Task HandleCharacterDeleteRequestAsync(
        GamePacket packet,
        string characterName,
        CancellationToken cancellationToken)
    {
        if (packet.ClientOperationId is { } operationId)
        {
            await HandleDurableCharacterDeleteAsync(
                operationId,
                characterName,
                cancellationToken);
            return;
        }

        if (_session.IsSecure)
        {
            await RejectMissingLifecycleIdentityAsync(
                CommandFamily.CharacterDelete,
                "delete",
                cancellationToken);
            return;
        }

        if (_characterLifecycleCommands is not null)
        {
            await RejectMixedLifecycleProfileAsync(
                CommandFamily.CharacterDelete,
                "delete",
                cancellationToken);
            return;
        }

        await HandleCompatibilityCharacterDeleteAsync(
            characterName,
            cancellationToken);
    }

    private async Task HandleDurableCharacterCreateAsync(
        Guid operationId,
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        if (!_session.IsSecure ||
            _characterLifecycleCommands is null ||
            operationId == Guid.Empty)
        {
            RecordLifecycleProviderUnavailable(
                CommandFamily.CharacterCreate,
                operationId,
                "secure transport or lifecycle provider is unavailable");
            return;
        }

        var command = new CharacterCreateCommand(
            operationId,
            CharacterLifecycleCommandContract.SingleCharacterSlot,
            character.Name,
            character.Gender,
            character.Camp,
            character.Profession,
            character.ZodiacType,
            character.Hair,
            character.Face,
            character.Faith);
        CharacterLifecycleExecutionResult execution;
        try
        {
            execution = await _characterLifecycleCommands.ExecuteAsync(
                CharacterCreateCommandEnvelope.Create(
                    _account.Id,
                    SecureLifecycleCorrelation(),
                    DateTimeOffset.UtcNow,
                    command),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.CharacterCreate,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            RecordLifecycleProviderUnavailable(
                CommandFamily.CharacterCreate,
                operationId,
                exception.Message);
            return;
        }

        await CompleteDurableCharacterLifecycleAsync(
            operationId,
            CommandFamily.CharacterCreate,
            execution,
            cancellationToken);
    }

    private async Task HandleDurableCharacterDeleteAsync(
        Guid operationId,
        string characterName,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        if (!_session.IsSecure ||
            _characterLifecycleCommands is null ||
            operationId == Guid.Empty)
        {
            RecordLifecycleProviderUnavailable(
                CommandFamily.CharacterDelete,
                operationId,
                "secure transport or lifecycle provider is unavailable");
            return;
        }

        var command = new CharacterDeleteCommand(
            operationId,
            CharacterLifecycleCommandContract.SingleCharacterSlot,
            characterName,
            _character?.Id,
            _character?.LifecycleVersion);
        CharacterLifecycleExecutionResult execution;
        try
        {
            execution = await _characterLifecycleCommands.ExecuteAsync(
                CharacterDeleteCommandEnvelope.Create(
                    _account.Id,
                    SecureLifecycleCorrelation(),
                    DateTimeOffset.UtcNow,
                    command),
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.CharacterDelete,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            RecordLifecycleProviderUnavailable(
                CommandFamily.CharacterDelete,
                operationId,
                exception.Message);
            return;
        }

        await CompleteDurableCharacterLifecycleAsync(
            operationId,
            CommandFamily.CharacterDelete,
            execution,
            cancellationToken);
    }

    private async Task CompleteDurableCharacterLifecycleAsync(
        Guid operationId,
        CommandFamily family,
        CharacterLifecycleExecutionResult execution,
        CancellationToken cancellationToken)
    {
        if (_account is null ||
            !TryResolveLifecycleTerminal(
                execution,
                out var disposition,
                out var resultCode,
                out var revision,
                out var outcome))
        {
            RecordLifecycleProviderUnavailable(
                family,
                operationId,
                $"unresolved lifecycle result {execution.Disposition}");
            return;
        }

        var receipt = execution.Receipt;
        if (receipt is not null &&
            (receipt.Family != family ||
             receipt.AccountId != _account.Id ||
             receipt.CharacterSlot !=
                CharacterLifecycleCommandContract
                    .SingleCharacterSlot))
        {
            RecordLifecycleProviderUnavailable(
                family,
                operationId,
                "receipt identity does not match the command");
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            outcome);

        if (!await RefreshCharacterSnapshotAsync(
                family == CommandFamily.CharacterCreate
                    ? "durable_create"
                    : "durable_delete",
                cancellationToken))
        {
            return;
        }

        if (execution.IsSuccess && receipt is not null)
        {
            var projectionMatches =
                family == CommandFamily.CharacterCreate
                    ? _character is not null &&
                      _character.Id == receipt.CharacterId &&
                      string.Equals(
                          _character.Name,
                          receipt.CharacterName,
                          StringComparison.Ordinal)
                    : _character is null;
            if (projectionMatches)
            {
                await _session.SendAsync(
                    family == CommandFamily.CharacterCreate
                        ? PacketBuilder.CreateRoleSuccess()
                        : PacketBuilder.DeleteRoleSuccess(),
                    cancellationToken,
                    family == CommandFamily.CharacterCreate
                        ? "DurableCreateRoleSuccess"
                        : "DurableDeleteRoleSuccess");
            }
            else
            {
                Console.WriteLine(
                    "[character] settled historical lifecycle receipt " +
                    $"against newer projection family={(ushort)family} " +
                    $"operationId={operationId}");
            }

            await SendCharacterPreviewAsync(cancellationToken);
        }
        else
        {
            await SendCharacterPreviewAsync(cancellationToken);
        }

        await _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)family,
                resultCode,
                revision,
                operationId),
            cancellationToken);
    }

    private async Task HandleCompatibilityCharacterCreateAsync(
        GameCharacter character,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.CharacterCreate);
        if (!_characterSnapshotLoaded ||
            _character is not null ||
            _characterLoadSnapshot is not null ||
            _characterSnapshotBootstrapPending)
        {
            CommandMetrics.Record(
                CommandFamily.CharacterCreate,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.PreconditionFailed);
            RejectCharacterSnapshot(
                "create",
                _characterSnapshotLoaded
                    ? "slot_not_empty"
                    : "snapshot_not_loaded");
            return;
        }

        GameCharacter created;
        try
        {
            LegacyPersistenceMetrics.Record(
                LegacyPersistenceOperation.CreateCharacter);
            created = await _store.CreateCharacterAsync(
                _account.Id,
                character,
                cancellationToken);
        }
        catch (CharacterSlotOccupiedException)
        {
            CommandMetrics.Record(
                CommandFamily.CharacterCreate,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.PreconditionFailed);
            await RefreshCharacterSnapshotAsync(
                "compatibility_create_conflict",
                cancellationToken);
            await SendCharacterPreviewAsync(cancellationToken);
            return;
        }

        if (!await RefreshCharacterSnapshotAsync(
                "compatibility_create",
                cancellationToken))
        {
            return;
        }
        if (_character is null ||
            _character.Id != created.Id ||
            !string.Equals(
                _character.Name,
                created.Name,
                StringComparison.Ordinal))
        {
            RejectCharacterSnapshot(
                "create",
                "mutation_snapshot_mismatch");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.CharacterCreate,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            CommandOutcome.Accepted);
        Console.WriteLine(
            $"[game] created compatibility character {created.Name}");
        await _session.SendAsync(
            PacketBuilder.CreateRoleSuccess(),
            cancellationToken,
            "CreateRoleSuccess");
    }

    private async Task HandleCompatibilityCharacterDeleteAsync(
        string characterName,
        CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return;
        }

        CommandMetrics.RecordUnsupportedLegacyIdentity(
            CommandFamily.CharacterDelete);
        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.DeleteCharacter);
        var deleted = await _store.DeleteCharacterAsync(
            _account.Id,
            characterName,
            cancellationToken);
        if (!await RefreshCharacterSnapshotAsync(
                "compatibility_delete",
                cancellationToken))
        {
            return;
        }
        if (!deleted || _character is not null)
        {
            CommandMetrics.Record(
                CommandFamily.CharacterDelete,
                CommandIdentityStrength.UnsupportedLegacyRetry,
                CommandOutcome.PreconditionFailed);
            await SendCharacterPreviewAsync(cancellationToken);
            return;
        }

        CommandMetrics.Record(
            CommandFamily.CharacterDelete,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            CommandOutcome.Accepted);
        Console.WriteLine(
            $"[game] deleted compatibility character {characterName}");
        await _session.SendAsync(
            PacketBuilder.DeleteRoleSuccess(),
            cancellationToken,
            "DeleteRoleSuccess");
    }

    private CommandConnectionCorrelation SecureLifecycleCorrelation() =>
        new(
            _commandConnectionId,
            CommandTransportKind.SecureTlsLegacy);

    private void RecordLifecycleProviderUnavailable(
        CommandFamily family,
        Guid operationId,
        string reason)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[character] durable lifecycle unresolved; operation " +
            $"remains pending family={(ushort)family} " +
            $"operationId={operationId}: {reason}");
    }

    private static bool TryResolveLifecycleTerminal(
        CharacterLifecycleExecutionResult execution,
        out SecureLegacyCommandDisposition disposition,
        out uint resultCode,
        out ulong revision,
        out CommandOutcome outcome)
    {
        disposition = SecureLegacyCommandDisposition.Rejected;
        resultCode = 0;
        revision = 0;
        outcome = CommandOutcome.ProviderUnavailable;

        if (execution.Receipt is { } receipt)
        {
            resultCode = (uint)receipt.Status;
            revision = checked((ulong)Math.Max(
                0,
                receipt.LifecycleVersion));
            disposition = receipt.Succeeded
                ? execution.Disposition switch
                {
                    CharacterLifecycleExecutionDisposition.Committed =>
                        SecureLegacyCommandDisposition.Applied,
                    CharacterLifecycleExecutionDisposition.Duplicate =>
                        SecureLegacyCommandDisposition.Replayed,
                    _ => SecureLegacyCommandDisposition.Rejected
                }
                : SecureLegacyCommandDisposition.Rejected;
            outcome = execution.Disposition switch
            {
                CharacterLifecycleExecutionDisposition.Committed =>
                    receipt.Succeeded
                        ? CommandOutcome.Accepted
                        : CommandOutcome.PreconditionFailed,
                CharacterLifecycleExecutionDisposition.Duplicate =>
                    CommandOutcome.Duplicate,
                CharacterLifecycleExecutionDisposition.TerminalRejected =>
                    CommandOutcome.PreconditionFailed,
                _ => CommandOutcome.ProviderUnavailable
            };
            return execution.Disposition is
                CharacterLifecycleExecutionDisposition.Committed or
                CharacterLifecycleExecutionDisposition.Duplicate or
                CharacterLifecycleExecutionDisposition.TerminalRejected;
        }

        switch (execution.Disposition)
        {
            case CharacterLifecycleExecutionDisposition
                .RequestHashConflict:
                disposition =
                    SecureLegacyCommandDisposition.Conflict;
                outcome = CommandOutcome.RequestHashConflict;
                return true;
            case CharacterLifecycleExecutionDisposition.InvalidIntent:
                outcome = CommandOutcome.InvalidIntent;
                return true;
            case CharacterLifecycleExecutionDisposition.AccountNotFound:
                outcome = CommandOutcome.PreconditionFailed;
                return true;
            default:
                return false;
        }
    }
}
