using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IDeveloperBagClearCommandExecutor?
        _developerBagClearCommands;

    private async Task HandleDurableDeveloperBagClearAsync(
        GamePacket packet,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (_developerBagClearCommands is null)
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperBagClear,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[item] Durable bag clearing is unavailable for this " +
                "storage provider.",
                cancellationToken);
            return;
        }

        if (!DeveloperBagClearCommandEnvelope.TryCreateCommand(
                clientOperationId,
                out var command))
        {
            CommandMetrics.Record(
                CommandFamily.DeveloperBagClear,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.InvalidIntent);
            await SendDeveloperItemFeedbackAsync(
                packet,
                "[item] Invalid durable bag-clear request.",
                cancellationToken);
            return;
        }

        var unownedEnvelope = DeveloperBagClearCommandEnvelope.Create(
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

        DeveloperBagClearExecutionResult execution;
        try
        {
            execution = await _developerBagClearCommands.ExecuteAsync(
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
                DeveloperBagClearExecutionDisposition
                    .RequestHashConflict =>
                    CommandOutcome.RequestHashConflict,
                DeveloperBagClearExecutionDisposition.InvalidIntent =>
                    CommandOutcome.InvalidIntent,
                _ => CommandOutcome.PreconditionFailed
            };
            CommandMetrics.Record(
                envelope.Family,
                envelope.IdentityStrength,
                outcome);
            var feedback = execution.Disposition switch
            {
                DeveloperBagClearExecutionDisposition
                    .RequestHashConflict =>
                    "[item] That operation ID was already used for a " +
                    "different request.",
                DeveloperBagClearExecutionDisposition.InvalidIntent =>
                    "[item] The durable bag-clear request is invalid.",
                _ => "[item] Bag not cleared: it is already empty or " +
                    "the character is unavailable."
            };
            await SendDeveloperItemFeedbackAsync(
                packet,
                feedback,
                cancellationToken);
            return;
        }

        var duplicate =
            execution.Disposition ==
            DeveloperBagClearExecutionDisposition.Duplicate;
        CommandMetrics.Record(
            envelope.Family,
            envelope.IdentityStrength,
            duplicate
                ? CommandOutcome.Duplicate
                : CommandOutcome.Accepted);
        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A successful bag clear returned no receipt.");

        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account.Id,
            _processRealmId,
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
                "A committed bag-clear character could not be reloaded.");
        }

        ApplyDeveloperItemGrantProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        foreach (var slot in receipt.RemovedSlots)
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagDelete(slot),
                cancellationToken,
                "DeveloperItemDurableClearBagDeleteAck");
        }

        await SendKitBagRefreshAsync(cancellationToken);
        await SendDeveloperItemFeedbackAsync(
            packet,
            duplicate
                ? "[item] Clear operation already completed; bag " +
                    "refreshed."
                : $"[item] Cleared {receipt.RemovedSlots.Count} bag " +
                    "item(s).",
            cancellationToken);
        Console.WriteLine(
            "[developer-item] durable clear completed " +
            $"account={_account.Id} character={_character.Name} " +
            $"removed={receipt.RemovedSlots.Count} " +
            $"outcome={(duplicate ? "duplicate" : "committed")}");
    }
}
