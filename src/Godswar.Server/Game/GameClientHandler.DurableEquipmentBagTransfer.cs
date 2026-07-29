using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const int DurableEquipmentBagTransferRequestBytes = 80;

    private readonly IEquipmentBagTransferCommandExecutor?
        _equipmentBagTransferCommands;

    private async Task HandleDurableEquipmentBagTransferAsync(
        int equipmentSlot,
        int kitBagSlot,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (_equipmentBagTransferCommands is null)
        {
            RecordDurableEquipmentBagTransferUnavailable(
                clientOperationId,
                "provider is not configured");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        EquipmentBagTransferExecutionResult execution;
        try
        {
            // A completed retry observes reversed occupancy. Resolve the
            // permanent operation before reading either mutable slot.
            execution =
                await _equipmentBagTransferCommands.TryReplayAsync(
                    subject,
                    clientOperationId,
                    equipmentSlot,
                    kitBagSlot,
                    cancellationToken);
            if (execution.Disposition ==
                EquipmentBagTransferDisposition.ReplayNotFound)
            {
                execution =
                    await ExecuteDurableEquipmentBagTransferAsync(
                        subject,
                        equipmentSlot,
                        kitBagSlot,
                        clientOperationId,
                        cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.EquipmentBagTransfer,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableEquipmentBagTransferUnavailable(
                clientOperationId,
                ex.Message);
            return;
        }

        if (!execution.IsDurable)
        {
            await HandleNonDurableEquipmentBagTransferOutcomeAsync(
                equipmentSlot,
                kitBagSlot,
                clientOperationId,
                execution.Disposition,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable equipment/bag transfer has no receipt.");
        try
        {
            ValidateDurableEquipmentBagTransferReceipt(
                equipmentSlot,
                kitBagSlot,
                receipt);
            await ReloadDurableEquipmentBagTransferProjectionAsync(
                cancellationToken,
                execution.Disposition ==
                    EquipmentBagTransferDisposition.Committed
                    ? receipt
                    : null);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.EquipmentBagTransfer,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableEquipmentBagTransferUnavailable(
                clientOperationId,
                $"projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.EquipmentBagTransfer,
            CommandIdentityStrength.ClientOperationId,
            MapDurableEquipmentBagTransferOutcome(
                execution.Disposition));
        await SendDurableEquipmentBagTransferReceiptAsync(
            clientOperationId,
            receipt,
            execution.Disposition,
            cancellationToken);
    }

    private async Task<EquipmentBagTransferExecutionResult>
        ExecuteDurableEquipmentBagTransferAsync(
            CommandSubject subject,
            int equipmentSlot,
            int kitBagSlot,
            Guid clientOperationId,
            CancellationToken cancellationToken)
    {
        var equipmentItem = EquipmentSlots.GetItem(
            _character!.Equipment,
            _character.Profession,
            equipmentSlot);
        var kitBagItem = KitBagSlots.GetItem(
            _character.KitBag,
            kitBagSlot);
        var mountRuntimeBlocked =
            equipmentSlot == EquipmentSlots.Mount &&
            (IsSkillCastPending(MountCatalog.RideSkillId) ||
             _registry.IsRuntimeStatusActive(
                 _session,
                 MountCatalog.RuntimeStatusKind,
                 DateTimeOffset.UtcNow));
        if (!EquipmentBagTransferCommandEnvelope.TryCreateCommand(
                clientOperationId,
                equipmentSlot,
                kitBagSlot,
                equipmentItem.ToCompactString(),
                kitBagItem.ToCompactString(),
                mountRuntimeBlocked,
                out var command))
        {
            return EquipmentBagTransferExecutionResult.InvalidIntent();
        }

        var envelope = EquipmentBagTransferCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        return await _equipmentBagTransferCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task
        HandleNonDurableEquipmentBagTransferOutcomeAsync(
            int equipmentSlot,
            int kitBagSlot,
            Guid clientOperationId,
            EquipmentBagTransferDisposition disposition,
            CancellationToken cancellationToken)
    {
        if (disposition ==
            EquipmentBagTransferDisposition.ReplayNotFound)
        {
            RecordDurableEquipmentBagTransferUnavailable(
                clientOperationId,
                "replay remained unresolved");
            return;
        }
        if (disposition is not (
                EquipmentBagTransferDisposition.RequestHashConflict or
                EquipmentBagTransferDisposition.InvalidIntent or
                EquipmentBagTransferDisposition.PreconditionFailed))
        {
            RecordDurableEquipmentBagTransferUnavailable(
                clientOperationId,
                $"unknown execution disposition {disposition}");
            return;
        }

        try
        {
            await ReloadDurableEquipmentBagTransferProjectionAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                CommandFamily.EquipmentBagTransfer,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordDurableEquipmentBagTransferUnavailable(
                clientOperationId,
                $"rejection projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            CommandFamily.EquipmentBagTransfer,
            CommandIdentityStrength.ClientOperationId,
            MapDurableEquipmentBagTransferOutcome(disposition));
        await SendDurableEquipmentBagTransferProjectionAsync(
            equipmentSlot,
            kitBagSlot,
            receiptStatus: null,
            clearOriginalKitBagItem: false,
            cancellationToken);
        await SendSecureEquipmentBagTransferResultAsync(
            clientOperationId,
            resultCode: 0,
            disposition ==
                EquipmentBagTransferDisposition.RequestHashConflict
                ? SecureLegacyCommandDisposition.Conflict
                : SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
    }

    private async Task SendDurableEquipmentBagTransferReceiptAsync(
        Guid clientOperationId,
        EquipmentBagTransferExecutionReceipt receipt,
        EquipmentBagTransferDisposition executionDisposition,
        CancellationToken cancellationToken)
    {
        var transferred = receipt.Status is
            EquipmentBagTransferResultStatus.Equipped or
            EquipmentBagTransferResultStatus.Unequipped;
        var committed =
            executionDisposition ==
                EquipmentBagTransferDisposition.Committed &&
            transferred;

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "DurableEquipmentBagTransferPlayerStatus");
        if (committed)
        {
            if (receipt.Status ==
                EquipmentBagTransferResultStatus.Unequipped)
            {
                var movedItem = CompactItemEntry.Parse(
                    receipt.ExpectedEquipmentCompactItemState);
                _pendingUnequipFollowup = movedItem.IsEmpty
                    ? null
                    : new PendingUnequipFollowup(
                        receipt.KitBagSlot,
                        movedItem.Id,
                        DateTime.UtcNow);
            }

            await _session.SendAsync(
                PacketBuilder.StorageItemEquipmentBagTransfer(
                    PacketBuilder.ToClientEquipmentSlot(
                        receipt.EquipmentSlot),
                    receipt.KitBagSlot),
                cancellationToken,
                "DurableStorageItemEquipmentBagTransferAck");
        }

        await SendDurableEquipmentBagTransferProjectionAsync(
            receipt.EquipmentSlot,
            receipt.KitBagSlot,
            receipt.Status,
            clearOriginalKitBagItem:
                !committed &&
                receipt.Status ==
                    EquipmentBagTransferResultStatus.Equipped,
            cancellationToken);
        await SendSecureEquipmentBagTransferResultAsync(
            clientOperationId,
            checked((uint)receipt.Status),
            executionDisposition switch
            {
                EquipmentBagTransferDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                EquipmentBagTransferDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            },
            receipt.InventoryRevision,
            cancellationToken);
    }

    private async Task SendDurableEquipmentBagTransferProjectionAsync(
        int equipmentSlot,
        int kitBagSlot,
        EquipmentBagTransferResultStatus? receiptStatus,
        bool clearOriginalKitBagItem,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        if (clearOriginalKitBagItem)
        {
            await _session.SendAsync(
                PacketBuilder.StorageItemKitBagDelete(kitBagSlot),
                cancellationToken,
                "DurableEquipmentBagTransferKitBagClear");
        }

        var equipment =
            PacketBuilder.EquipmentItemSnapshot(
                _character,
                equipmentSlot);
        if (equipment.Length == 0)
        {
            equipment =
                PacketBuilder.EquipmentItemClearSnapshot(equipmentSlot);
        }
        await _session.SendAsync(
            equipment,
            cancellationToken,
            "DurableEquipmentBagTransferEquipmentRefresh");
        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "DurableEquipmentBagTransferVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "DurableEquipmentBagTransferDetailRefresh");
        await BroadcastEquipmentRefreshAsync(
            receiptStatus?.ToString() ?? "rejected",
            cancellationToken);
    }

    private async Task
        RejectUnsupportedDurableEquipmentBagTransferAsync(
            int equipmentSlot,
            int kitBagSlot,
            Guid clientOperationId,
            CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            return;
        }

        CommandMetrics.Record(
            CommandFamily.EquipmentBagTransfer,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.InvalidIntent);
        await SendEquipmentBagTransferRejectionRefreshAsync(
            equipmentSlot,
            kitBagSlot,
            cancellationToken);
        await SendSecureEquipmentBagTransferResultAsync(
            clientOperationId,
            resultCode: 0,
            SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
    }

    private ValueTask SendSecureEquipmentBagTransferResultAsync(
        Guid clientOperationId,
        uint resultCode,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Durable equipment/bag transfer requires secure transport.");
        }

        return _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)CommandFamily.EquipmentBagTransfer,
                resultCode,
                checked((ulong)inventoryRevision),
                clientOperationId),
            cancellationToken);
    }

    private void ValidateDurableEquipmentBagTransferReceipt(
        int requestedEquipmentSlot,
        int requestedKitBagSlot,
        EquipmentBagTransferExecutionReceipt receipt)
    {
        if (receipt.Family !=
                CommandFamily.EquipmentBagTransfer ||
            receipt.CharacterId != _character!.Id ||
            receipt.EquipmentSlot != requestedEquipmentSlot ||
            receipt.KitBagSlot != requestedKitBagSlot)
        {
            throw new InvalidDataException(
                "The equipment/bag transfer receipt identity is " +
                "inconsistent.");
        }
    }

    private void ValidateCommittedEquipmentBagTransferProjection(
        GameCharacter projectedCharacter,
        EquipmentBagTransferExecutionReceipt receipt)
    {
        var projectedEquipment = EquipmentSlots.GetItem(
                projectedCharacter.Equipment,
                projectedCharacter.Profession,
                receipt.EquipmentSlot)
            .ToCompactString();
        var projectedKitBag = KitBagSlots.GetItem(
                projectedCharacter.KitBag,
                receipt.KitBagSlot)
            .ToCompactString();
        var valid = receipt.Status switch
        {
            EquipmentBagTransferResultStatus.Equipped =>
                projectedEquipment ==
                    receipt.ExpectedKitBagCompactItemState &&
                projectedKitBag == "[]",
            EquipmentBagTransferResultStatus.Unequipped =>
                projectedEquipment == "[]" &&
                projectedKitBag ==
                    receipt.ExpectedEquipmentCompactItemState,
            _ => false
        };
        if (!valid)
        {
            throw new InvalidDataException(
                "The committed equipment/bag transfer projection does " +
                "not contain the durable result.");
        }
    }

    private void RecordDurableEquipmentBagTransferUnavailable(
        Guid clientOperationId,
        string reason)
    {
        CommandMetrics.Record(
            CommandFamily.EquipmentBagTransfer,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[inventory] durable equipment/bag transfer unresolved; " +
            $"operation remains pending account={_account?.Id} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"operationId={clientOperationId}: {reason}");
    }

    private static CommandOutcome
        MapDurableEquipmentBagTransferOutcome(
            EquipmentBagTransferDisposition disposition) =>
        disposition switch
        {
            EquipmentBagTransferDisposition.Committed =>
                CommandOutcome.Accepted,
            EquipmentBagTransferDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            EquipmentBagTransferDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            EquipmentBagTransferDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            EquipmentBagTransferDisposition.TerminalRejected or
                EquipmentBagTransferDisposition.PreconditionFailed or
                EquipmentBagTransferDisposition.ReplayNotFound =>
                    CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
