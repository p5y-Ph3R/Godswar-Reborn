using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleDurableDeveloperItemGrantAsync(
        GamePacket packet,
        uint itemId,
        string displayName,
        int quantity,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (_developerItemGrantCommands is null)
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperItemGrant,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[item] Durable item grants are unavailable for this " +
                "storage provider.",
                cancellationToken);
            return;
        }

        if (!DeveloperItemGrantCommandEnvelope.TryCreateCommand(
                itemId,
                quantity,
                clientOperationId,
                out var command))
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperItemGrant,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.InvalidIntent);
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[item] Invalid durable grant request.",
                cancellationToken);
            return;
        }

        var unownedEnvelope = DeveloperItemGrantCommandEnvelope.Create(
            new CommandSubject(_account.Id, _character.Id),
            new CommandConnectionCorrelation(
                _commandConnectionId,
                _session.IsSecure
                    ? CommandTransportKind.SecureTlsLegacy
                    : CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command);
        if (!TryBindCurrentPlayerOwnership(
                unownedEnvelope,
                out var envelope,
                out var ownership))
        {
            return;
        }

        DeveloperItemGrantExecutionResult execution;
        try
        {
            execution = await _developerItemGrantCommands.ExecuteAsync(
                envelope,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch
        {
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                CommandOutcome.ProviderUnavailable);
            throw;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        if (!execution.IsSuccess)
        {
            var outcome = execution.Disposition switch
            {
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict =>
                    CommandOutcome.RequestHashConflict,
                DeveloperItemGrantExecutionDisposition.InvalidIntent =>
                    CommandOutcome.InvalidIntent,
                _ => CommandOutcome.PreconditionFailed
            };
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                outcome);
            var feedback = execution.Disposition switch
            {
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict =>
                    "[item] That operation ID was already used for a " +
                    "different request.",
                DeveloperItemGrantExecutionDisposition.InvalidIntent =>
                    "[item] The requested item is not allowlisted.",
                _ =>
                    "[item] Not added: the character or kit-bag " +
                    "precondition failed."
            };
            await SendDeveloperItemFeedbackAsync(
                packet,
                feedback,
                cancellationToken);
            Console.WriteLine(
                "[developer-item] durable grant rejected " +
                $"account={_account.Id} character={_character.Name} " +
                $"item={itemId} quantity={quantity} " +
                $"outcome={outcome}");
            return;
        }

        var duplicate =
            execution.Disposition ==
            DeveloperItemGrantExecutionDisposition.Duplicate;
        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            duplicate
                ? CommandOutcome.Duplicate
                : CommandOutcome.Accepted);

        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account.Id,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character.Id)
        {
            throw new InvalidDataException(
                "A committed inventory grant character could not be " +
                "reloaded.");
        }

        ApplyDeveloperItemGrantProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        await SendKitBagRefreshAsync(cancellationToken);
        await SendDeveloperItemFeedbackAsync(
            packet,
            duplicate
                ? "[item] Operation already completed; bag refreshed."
                : $"[item] Added {quantity} {displayName}.",
            cancellationToken);
        Console.WriteLine(
            "[developer-item] durable grant completed " +
            $"account={_account.Id} character={_character.Name} " +
            $"item={itemId} quantity={quantity} " +
            $"outcome={(duplicate ? "duplicate" : "committed")}");
    }

    internal static void ApplyDeveloperItemGrantProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "An inventory projection cannot change character " +
                "identity.");
        }

        // A bag-only command must not replace the live mutable aggregate.
        // Position, vitals, and other runtime fields can be newer than their
        // asynchronously persisted snapshot.
        liveCharacter.KitBag = persistedCharacter.KitBag;
    }
}
