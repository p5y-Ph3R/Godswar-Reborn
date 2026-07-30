using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IHolyStoneCommandExecutor? _holyStoneCommands;

    private async Task HandleDurableHolyStoneAsync(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        Guid clientOperationId,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        var family = HolyStoneProtocol.Family(intent.Operation);
        if (_holyStoneCommands is null)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                clientOperationId,
                "provider is not configured");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        var kitBagBeforeExecution = _character.KitBag;
        HolyStoneExecutionResult execution;
        try
        {
            // Replays must resolve before any mutable target or material
            // snapshot is captured. A successful first attempt has already
            // changed both states.
            execution = await _holyStoneCommands.TryReplayAsync(
                subject,
                ownership,
                intent.Operation,
                clientOperationId,
                cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return;
            }

            if (execution.Disposition ==
                HolyStoneExecutionDisposition.ReplayNotFound)
            {
                execution = await ExecuteDurableHolyStoneAsync(
                    subject,
                    npcId,
                    dialogIndex,
                    intent,
                    clientOperationId,
                    ownership,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception ex)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                clientOperationId,
                ex.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        if (!execution.IsDurable)
        {
            await HandleNonDurableHolyStoneOutcomeAsync(
                npcId,
                dialogIndex,
                intent,
                clientOperationId,
                execution.Disposition,
                kitBagBeforeExecution,
                ownership,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Holy Stone outcome has no receipt.");
        try
        {
            ValidateHolyStoneReceiptIdentity(
                _character.Id,
                npcId,
                dialogIndex,
                intent,
                receipt);
            await ReloadDurableHolyStoneProjectionAsync(
                execution.Disposition ==
                    HolyStoneExecutionDisposition.Committed
                    ? receipt
                    : null,
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                clientOperationId,
                $"projection reload failed: {ex.Message}");
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            MapHolyStoneOutcome(execution.Disposition));
        await SendDurableHolyStoneReceiptAsync(
            npcId,
            dialogIndex,
            clientOperationId,
            receipt,
            execution.Disposition,
            kitBagBeforeExecution,
            cancellationToken);
    }

    private async Task<HolyStoneExecutionResult>
        ExecuteDurableHolyStoneAsync(
            CommandSubject subject,
            uint npcId,
            int dialogIndex,
            HolyStoneWireIntent intent,
            Guid clientOperationId,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken)
    {
        var expectedTarget = intent.TargetLocation switch
        {
            HolyStoneTargetLocation.Equipment =>
                EquipmentSlots.GetItem(
                    _character!.Equipment,
                    _character.Profession,
                    intent.TargetSlot),
            HolyStoneTargetLocation.KitBag =>
                KitBagSlots.GetItem(
                    _character!.KitBag,
                    intent.TargetSlot),
            _ => CompactItemEntry.Empty
        };
        var expectedStone =
            intent.Operation == HolyStoneCommandOperation.Mount
                ? KitBagSlots.GetItem(
                    _character!.KitBag,
                    intent.StoneKitBagSlot)
                : CompactItemEntry.Empty;
        if (!HolyStoneCommandEnvelope.TryCreateCommand(
                clientOperationId,
                intent.Operation,
                checked((int)npcId),
                dialogIndex,
                intent.TargetLocation,
                intent.TargetSlot,
                expectedTarget.ToCompactString(),
                intent.SocketIndex,
                intent.StoneKitBagSlot,
                expectedStone.ToCompactString(),
                out var command))
        {
            return HolyStoneExecutionResult.InvalidIntent();
        }

        var envelope = HolyStoneCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command) with
        {
            Ownership = ownership
        };
        return await _holyStoneCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task HandleNonDurableHolyStoneOutcomeAsync(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        Guid clientOperationId,
        HolyStoneExecutionDisposition disposition,
        string kitBagBeforeExecution,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var family = HolyStoneProtocol.Family(intent.Operation);
        if (disposition ==
            HolyStoneExecutionDisposition.ReplayNotFound)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                clientOperationId,
                "replay remained unresolved");
            return;
        }
        if (disposition is not (
                HolyStoneExecutionDisposition.RequestHashConflict or
                HolyStoneExecutionDisposition.InvalidIntent or
                HolyStoneExecutionDisposition.PreconditionFailed))
        {
            RecordHolyStoneProviderUnavailable(
                family,
                clientOperationId,
                $"unknown execution disposition {disposition}");
            return;
        }

        try
        {
            await ReloadDurableHolyStoneProjectionAsync(
                committedReceipt: null,
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                clientOperationId,
                $"rejection projection reload failed: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            MapHolyStoneOutcome(disposition));
        await SendHolyStoneProjectionAndResultAsync(
            npcId,
            dialogIndex,
            clientOperationId,
            family,
            HolyStoneNativeResults.WrongSelectionSubId,
            disposition ==
                HolyStoneExecutionDisposition.RequestHashConflict
                ? SecureLegacyCommandDisposition.Conflict
                : SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            kitBagBeforeExecution,
            cancellationToken);
    }

    private void RecordHolyStoneProviderUnavailable(
        CommandFamily family,
        Guid clientOperationId,
        string reason)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[holy-stone] durable operation remains pending " +
            $"account={_account?.Id.ToString() ?? "<none>"} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"family={family} operation={clientOperationId} " +
            $"reason={reason}");
    }

    private static CommandOutcome MapHolyStoneOutcome(
        HolyStoneExecutionDisposition disposition) =>
        disposition switch
        {
            HolyStoneExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            HolyStoneExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            HolyStoneExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            HolyStoneExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            HolyStoneExecutionDisposition.TerminalRejected or
                HolyStoneExecutionDisposition.PreconditionFailed or
                HolyStoneExecutionDisposition.ReplayNotFound =>
                CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
