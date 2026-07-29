using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int DurableKitBagDeleteRequestBytes = 28;

    private readonly IKitBagItemDeleteCommandExecutor?
        _kitBagItemDeleteCommands;

    private async Task HandleDurableKitBagItemDeleteAsync(
        int kitBagSlot,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        if (_kitBagItemDeleteCommands is null)
        {
            RecordDurableKitBagDeleteUnavailable(
                clientOperationId,
                "provider is not configured");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        KitBagItemDeleteExecutionResult execution;
        try
        {
            // A retry can arrive after the original deletion made this slot
            // empty. The permanent inbox must therefore be consulted before
            // capturing any current slot state.
            execution =
                await _kitBagItemDeleteCommands.TryReplayAsync(
                    subject,
                    clientOperationId,
                    cancellationToken);
            if (execution.Disposition ==
                KitBagItemDeleteExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteDurableKitBagDeleteAsync(
                    subject,
                    kitBagSlot,
                    clientOperationId,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.KitBagItemDelete,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableKitBagDeleteUnavailable(
                clientOperationId,
                ex.Message);
            return;
        }

        if (!execution.IsDurable)
        {
            await HandleNonDurableKitBagDeleteOutcomeAsync(
                clientOperationId,
                execution.Disposition,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable kit-bag deletion has no receipt.");
        try
        {
            ValidateDurableKitBagDeleteReceipt(
                kitBagSlot,
                receipt);
            await ReloadDurableKitBagDeleteProjectionAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.KitBagItemDelete,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableKitBagDeleteUnavailable(
                clientOperationId,
                $"projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.KitBagItemDelete,
            CommandIdentityStrength.ClientOperationId,
            MapDurableKitBagDeleteOutcome(execution.Disposition));
        await SendDurableKitBagDeleteReceiptAsync(
            clientOperationId,
            receipt,
            execution.Disposition,
            cancellationToken);
    }

    private async Task<KitBagItemDeleteExecutionResult>
        ExecuteDurableKitBagDeleteAsync(
            CommandSubject subject,
            int kitBagSlot,
            Guid clientOperationId,
            CancellationToken cancellationToken)
    {
        var expectedItem = KitBagSlots.GetItem(
            _character!.KitBag,
            kitBagSlot);
        if (!KitBagItemDeleteCommandEnvelope.TryCreateCommand(
                clientOperationId,
                kitBagSlot,
                expectedItem.ToCompactString(),
                out var command))
        {
            return KitBagItemDeleteExecutionResult.InvalidIntent();
        }

        var envelope = KitBagItemDeleteCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        return await _kitBagItemDeleteCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task HandleNonDurableKitBagDeleteOutcomeAsync(
        Guid clientOperationId,
        KitBagItemDeleteExecutionDisposition disposition,
        CancellationToken cancellationToken)
    {
        if (disposition ==
            KitBagItemDeleteExecutionDisposition.ReplayNotFound)
        {
            RecordDurableKitBagDeleteUnavailable(
                clientOperationId,
                "replay remained unresolved");
            return;
        }

        if (disposition is not (
                KitBagItemDeleteExecutionDisposition
                    .RequestHashConflict or
                KitBagItemDeleteExecutionDisposition.InvalidIntent or
                KitBagItemDeleteExecutionDisposition
                    .PreconditionFailed))
        {
            RecordDurableKitBagDeleteUnavailable(
                clientOperationId,
                $"unknown execution disposition {disposition}");
            return;
        }

        try
        {
            await ReloadDurableKitBagDeleteProjectionAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.KitBagItemDelete,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableKitBagDeleteUnavailable(
                clientOperationId,
                $"rejection projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.KitBagItemDelete,
            CommandIdentityStrength.ClientOperationId,
            MapDurableKitBagDeleteOutcome(disposition));
        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureKitBagDeleteResultAsync(
            clientOperationId,
            resultCode: 0,
            disposition ==
                KitBagItemDeleteExecutionDisposition
                    .RequestHashConflict
                ? SecureLegacyCommandDisposition.Conflict
                : SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
    }

    private async Task SendDurableKitBagDeleteReceiptAsync(
        Guid clientOperationId,
        KitBagItemDeleteExecutionReceipt receipt,
        KitBagItemDeleteExecutionDisposition executionDisposition,
        CancellationToken cancellationToken)
    {
        if (receipt.Status ==
            KitBagItemDeleteResultStatus.Deleted)
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagDelete(
                    receipt.KitBagSlot),
                cancellationToken,
                "DurableStorageItemKitBagDeleteAck");
        }

        // Both success and rejection refresh the authoritative bag. The
        // authenticated 0x0102 result is deliberately last so the shim does
        // not retire its UUID before all stock-client projections are sent.
        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureKitBagDeleteResultAsync(
            clientOperationId,
            checked((uint)receipt.Status),
            executionDisposition switch
            {
                KitBagItemDeleteExecutionDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                KitBagItemDeleteExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            },
            receipt.InventoryRevision,
            cancellationToken);
    }

    private ValueTask SendSecureKitBagDeleteResultAsync(
        Guid clientOperationId,
        uint resultCode,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Durable kit-bag deletion requires secure transport.");
        }

        return _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)CommandFamily.KitBagItemDelete,
                resultCode,
                checked((ulong)inventoryRevision),
                clientOperationId),
            cancellationToken);
    }

    private async Task ReloadDurableKitBagDeleteProjectionAsync(
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
                "The durable kit-bag delete character could not be " +
                "reloaded.");
        }

        ApplyDurableKitBagDeleteProjection(
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

    internal static void ApplyDurableKitBagDeleteProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "A kit-bag delete projection cannot change character " +
                "identity.");
        }

        liveCharacter.KitBag = persistedCharacter.KitBag;
    }

    private void ValidateDurableKitBagDeleteReceipt(
        int requestedKitBagSlot,
        KitBagItemDeleteExecutionReceipt receipt)
    {
        if (receipt.Family != CommandFamily.KitBagItemDelete ||
            receipt.CharacterId != _character!.Id ||
            receipt.KitBagSlot != requestedKitBagSlot)
        {
            throw new InvalidDataException(
                "The kit-bag delete receipt identity is inconsistent.");
        }
    }

    private void RecordDurableKitBagDeleteUnavailable(
        Guid clientOperationId,
        string reason)
    {
        CommandMetrics.Record(
            CommandFamily.KitBagItemDelete,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[inventory] durable kit-bag delete unresolved; operation " +
            $"remains pending account={_account?.Id} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"operationId={clientOperationId}: {reason}");
    }

    private static CommandOutcome MapDurableKitBagDeleteOutcome(
        KitBagItemDeleteExecutionDisposition disposition) =>
        disposition switch
        {
            KitBagItemDeleteExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            KitBagItemDeleteExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            KitBagItemDeleteExecutionDisposition
                .RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            KitBagItemDeleteExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            KitBagItemDeleteExecutionDisposition.TerminalRejected or
                KitBagItemDeleteExecutionDisposition
                    .PreconditionFailed or
                KitBagItemDeleteExecutionDisposition.ReplayNotFound =>
                CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
