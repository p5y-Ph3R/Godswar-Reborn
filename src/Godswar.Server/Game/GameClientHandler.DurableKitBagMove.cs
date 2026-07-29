using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int DurableKitBagMoveCompactRequestBytes = 20;
    private const int DurableKitBagMoveDetailedRequestBytes = 80;

    private readonly IKitBagItemMoveCommandExecutor?
        _kitBagItemMoveCommands;

    private async Task HandleDurableKitBagItemMoveAsync(
        int sourceKitBagSlot,
        int destinationKitBagSlot,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (_kitBagItemMoveCommands is null)
        {
            RecordDurableKitBagMoveUnavailable(
                clientOperationId,
                "provider is not configured");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        KitBagItemMoveExecutionResult execution;
        try
        {
            // A retry after a completed swap sees reversed live slots. Resolve
            // the permanent UUID before capturing either current item state.
            execution = await _kitBagItemMoveCommands.TryReplayAsync(
                subject,
                clientOperationId,
                sourceKitBagSlot,
                destinationKitBagSlot,
                cancellationToken);
            if (execution.Disposition ==
                KitBagItemMoveExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteDurableKitBagItemMoveAsync(
                    subject,
                    sourceKitBagSlot,
                    destinationKitBagSlot,
                    clientOperationId,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.KitBagItemMove,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableKitBagMoveUnavailable(
                clientOperationId,
                ex.Message);
            return;
        }

        if (!execution.IsDurable)
        {
            await HandleNonDurableKitBagMoveOutcomeAsync(
                clientOperationId,
                execution.Disposition,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable kit-bag move has no receipt.");
        try
        {
            ValidateDurableKitBagMoveReceipt(
                sourceKitBagSlot,
                destinationKitBagSlot,
                receipt);
            await ReloadDurableKitBagMoveProjectionAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.KitBagItemMove,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableKitBagMoveUnavailable(
                clientOperationId,
                $"projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.KitBagItemMove,
            CommandIdentityStrength.ClientOperationId,
            MapDurableKitBagMoveOutcome(execution.Disposition));
        await SendDurableKitBagMoveReceiptAsync(
            clientOperationId,
            receipt,
            execution.Disposition,
            cancellationToken);
    }

    private async Task<KitBagItemMoveExecutionResult>
        ExecuteDurableKitBagItemMoveAsync(
            CommandSubject subject,
            int sourceKitBagSlot,
            int destinationKitBagSlot,
            Guid clientOperationId,
            CancellationToken cancellationToken)
    {
        var sourceItem = KitBagSlots.GetItem(
            _character!.KitBag,
            sourceKitBagSlot);
        var destinationItem = KitBagSlots.GetItem(
            _character.KitBag,
            destinationKitBagSlot);
        if (!KitBagItemMoveCommandEnvelope.TryCreateCommand(
                clientOperationId,
                sourceKitBagSlot,
                destinationKitBagSlot,
                sourceItem.ToCompactString(),
                destinationItem.ToCompactString(),
                out var command))
        {
            return KitBagItemMoveExecutionResult.InvalidIntent();
        }

        var envelope = KitBagItemMoveCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        return await _kitBagItemMoveCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task HandleNonDurableKitBagMoveOutcomeAsync(
        Guid clientOperationId,
        KitBagItemMoveExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition ==
            KitBagItemMoveExecutionDisposition.ReplayNotFound)
        {
            RecordDurableKitBagMoveUnavailable(
                clientOperationId,
                "replay remained unresolved");
            return;
        }
        if (disposition is not (
                KitBagItemMoveExecutionDisposition
                    .RequestHashConflict or
                KitBagItemMoveExecutionDisposition.InvalidIntent or
                KitBagItemMoveExecutionDisposition
                    .PreconditionFailed))
        {
            RecordDurableKitBagMoveUnavailable(
                clientOperationId,
                $"unknown execution disposition {disposition}");
            return;
        }

        try
        {
            await ReloadDurableKitBagMoveProjectionAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.KitBagItemMove,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableKitBagMoveUnavailable(
                clientOperationId,
                $"rejection projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.KitBagItemMove,
            CommandIdentityStrength.ClientOperationId,
            MapDurableKitBagMoveOutcome(disposition));
        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureKitBagMoveResultAsync(
            clientOperationId,
            resultCode: 0,
            disposition ==
                KitBagItemMoveExecutionDisposition
                    .RequestHashConflict
                ? SecureLegacyCommandDisposition.Conflict
                : SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
    }

    private async Task SendDurableKitBagMoveReceiptAsync(
        Guid clientOperationId,
        KitBagItemMoveExecutionReceipt receipt,
        KitBagItemMoveExecutionDisposition executionDisposition,
        CancellationToken cancellationToken)
    {
        var moved = receipt.Status is
            KitBagItemMoveResultStatus.Moved or
            KitBagItemMoveResultStatus.Swapped;
        if (executionDisposition ==
                KitBagItemMoveExecutionDisposition.Committed &&
            moved)
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagMove(
                    receipt.SourceKitBagSlot,
                    receipt.DestinationKitBagSlot),
                cancellationToken,
                "DurableStorageItemKitBagMoveAck");
        }

        // A replayed swap must not receive another non-idempotent move ACK.
        // The full refresh reconciles both slots before the UUID is retired.
        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureKitBagMoveResultAsync(
            clientOperationId,
            checked((uint)receipt.Status),
            executionDisposition switch
            {
                KitBagItemMoveExecutionDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                KitBagItemMoveExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            },
            receipt.InventoryRevision,
            cancellationToken);
    }

    private async Task RejectUnsupportedDurableKitBagItemMoveAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            return;
        }

        CommandMetrics.Record(
            CommandFamily.KitBagItemMove,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.InvalidIntent);
        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureKitBagMoveResultAsync(
            clientOperationId,
            resultCode: 0,
            SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
    }

    private ValueTask SendSecureKitBagMoveResultAsync(
        Guid clientOperationId,
        uint resultCode,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Durable kit-bag movement requires secure transport.");
        }

        return _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)CommandFamily.KitBagItemMove,
                resultCode,
                checked((ulong)inventoryRevision),
                clientOperationId),
            cancellationToken);
    }

    private async Task ReloadDurableKitBagMoveProjectionAsync(
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            cancellationToken);
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable kit-bag move character could not be reloaded.");
        }

        ApplyDurableKitBagMoveProjection(
            _character,
            hydrated.Character);
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
    }

    internal static void ApplyDurableKitBagMoveProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "A kit-bag move projection cannot change character " +
                "identity.");
        }

        liveCharacter.KitBag = persistedCharacter.KitBag;
    }

    private void ValidateDurableKitBagMoveReceipt(
        int requestedSourceSlot,
        int requestedDestinationSlot,
        KitBagItemMoveExecutionReceipt receipt)
    {
        if (receipt.Family != CommandFamily.KitBagItemMove ||
            receipt.CharacterId != _character!.Id ||
            receipt.SourceKitBagSlot != requestedSourceSlot ||
            receipt.DestinationKitBagSlot != requestedDestinationSlot)
        {
            throw new InvalidDataException(
                "The kit-bag move receipt identity is inconsistent.");
        }
    }

    private void RecordDurableKitBagMoveUnavailable(
        Guid clientOperationId,
        string reason)
    {
        CommandMetrics.Record(
            CommandFamily.KitBagItemMove,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[inventory] durable kit-bag move unresolved; operation " +
            $"remains pending account={_account?.Id} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"operationId={clientOperationId}: {reason}");
    }

    private static CommandOutcome MapDurableKitBagMoveOutcome(
        KitBagItemMoveExecutionDisposition disposition) =>
        disposition switch
        {
            KitBagItemMoveExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            KitBagItemMoveExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            KitBagItemMoveExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            KitBagItemMoveExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            KitBagItemMoveExecutionDisposition.TerminalRejected or
                KitBagItemMoveExecutionDisposition
                    .PreconditionFailed or
                KitBagItemMoveExecutionDisposition.ReplayNotFound =>
                    CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
