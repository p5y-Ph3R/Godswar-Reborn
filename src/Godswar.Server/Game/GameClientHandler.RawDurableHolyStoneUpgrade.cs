using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleRawDurableHolyStoneAsync(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        CancellationToken cancellationToken)
    {
        if (_session.IsSecure ||
            _account is null ||
            _character is null ||
            intent.Operation is not (
                HolyStoneCommandOperation.Mount or
                HolyStoneCommandOperation.Remove or
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit or
                HolyStoneCommandOperation.MountGearDrill))
        {
            return;
        }
        if (!AllowLegacyPlayerMutationFallback("holy_stone"))
        {
            return;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return;
        }

        var family = HolyStoneProtocol.Family(intent.Operation);
        var identity = HolyStoneOperationIdentity.RawLocalServer(
            Guid.NewGuid(),
            _commandConnectionId);
        if (_holyStoneCommands is null)
        {
            RecordRawHolyStoneProviderUnavailable(
                family,
                identity.OperationId,
                "provider is not configured");
            await SendRawHolyStoneWrongSelectionAsync(
                npcId,
                dialogIndex,
                cancellationToken);
            return;
        }

        var bagBeforeExecution = _character.KitBag;
        HolyStoneExecutionResult execution;
        try
        {
            execution = await ExecuteRawDurableHolyStoneAsync(
                new CommandSubject(_account.Id, _character.Id),
                npcId,
                dialogIndex,
                intent,
                identity,
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ServerOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            RecordRawHolyStoneProviderUnavailable(
                family,
                identity.OperationId,
                exception.Message);
            await SendRawHolyStoneWrongSelectionAsync(
                npcId,
                dialogIndex,
                cancellationToken);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        if (!execution.IsDurable)
        {
            await HandleRawNonDurableHolyStoneAsync(
                npcId,
                dialogIndex,
                family,
                identity.OperationId,
                execution.Disposition,
                intent.Operation,
                bagBeforeExecution,
                ownership,
                cancellationToken);
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable raw Holy Stone outcome has no receipt.");
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
                CommandIdentityStrength.ServerOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            RecordRawHolyStoneProviderUnavailable(
                family,
                identity.OperationId,
                $"projection reload failed: {exception.Message}");
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ServerOperationId,
            MapHolyStoneOutcome(execution.Disposition));
        await SendRawHolyStoneReceiptAsync(
            npcId,
            dialogIndex,
            receipt,
            bagBeforeExecution,
            cancellationToken);
    }

    private async Task<HolyStoneExecutionResult>
        ExecuteRawDurableHolyStoneAsync(
            CommandSubject subject,
            uint npcId,
            int dialogIndex,
            HolyStoneWireIntent intent,
            HolyStoneOperationIdentity identity,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken)
    {
        var character = _character ??
            throw new InvalidOperationException(
                "A raw Holy Stone command requires a character.");
        var expectedTarget = intent.TargetLocation switch
        {
            HolyStoneTargetLocation.Equipment =>
                EquipmentSlots.GetItem(
                    character.Equipment,
                    character.Profession,
                    intent.TargetSlot),
            HolyStoneTargetLocation.KitBag => KitBagSlots.GetItem(
                character.KitBag,
                intent.TargetSlot),
            _ => CompactItemEntry.Empty
        };
        var expectedEclipse = KitBagSlots.GetItem(
            character.KitBag,
            intent.StoneKitBagSlot);
        var expectedCatalyst =
            intent.CatalystKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot
                ? KitBagSlots.GetItem(
                    character.KitBag,
                    intent.CatalystKitBagSlot)
                : CompactItemEntry.Empty;
        var expectedThirdMaterial =
            intent.Operation == HolyStoneCommandOperation.Combine &&
            intent.ThirdMaterialKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot
                ? KitBagSlots.GetItem(
                    character.KitBag,
                    intent.ThirdMaterialKitBagSlot)
                : CompactItemEntry.Empty;
        if (!HolyStoneCommandEnvelope.TryCreateCommand(
                identity,
                intent.Operation,
                checked((int)npcId),
                dialogIndex,
                intent.TargetLocation,
                intent.TargetSlot,
                expectedTarget.ToCompactString(),
                intent.SocketIndex,
                intent.StoneKitBagSlot,
                expectedEclipse.ToCompactString(),
                intent.CatalystKitBagSlot,
                expectedCatalyst.ToCompactString(),
                intent.ThirdMaterialKitBagSlot,
                expectedThirdMaterial.ToCompactString(),
                out var command))
        {
            return HolyStoneExecutionResult.InvalidIntent();
        }

        var envelope = HolyStoneCommandEnvelope.CreateRawLocal(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.LegacyTcp),
            DateTimeOffset.UtcNow,
            command) with
        {
            Ownership = ownership
        };
        return await _holyStoneCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task HandleRawNonDurableHolyStoneAsync(
        uint npcId,
        int dialogIndex,
        CommandFamily family,
        Guid operationId,
        HolyStoneExecutionDisposition disposition,
        HolyStoneCommandOperation operation,
        string bagBeforeExecution,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (disposition is not (
                HolyStoneExecutionDisposition.RequestHashConflict or
                HolyStoneExecutionDisposition.InvalidIntent or
                HolyStoneExecutionDisposition.PreconditionFailed))
        {
            RecordRawHolyStoneProviderUnavailable(
                family,
                operationId,
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
                CommandIdentityStrength.ServerOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception exception)
        {
            RecordRawHolyStoneProviderUnavailable(
                family,
                operationId,
                $"rejection projection reload failed: {exception.Message}");
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ServerOperationId,
            MapHolyStoneOutcome(disposition));
        await SendRawHolyStoneResultAndProjectionAsync(
            npcId,
            dialogIndex,
            HolyStoneNativeResults.WrongSelectionSubId,
            "rejected",
            operation,
            bagBeforeExecution,
            cancellationToken);
    }

    private async Task SendRawHolyStoneReceiptAsync(
        uint npcId,
        int dialogIndex,
        HolyStoneExecutionReceipt receipt,
        string bagBeforeExecution,
        CancellationToken cancellationToken) =>
        await SendRawHolyStoneResultAndProjectionAsync(
            npcId,
            dialogIndex,
            receipt.NativeResultSubId,
            receipt.Status.ToString(),
            receipt.Operation,
            bagBeforeExecution,
            cancellationToken);

    private async Task SendRawHolyStoneResultAndProjectionAsync(
        uint npcId,
        int dialogIndex,
        int nativeResultSubId,
        string reason,
        HolyStoneCommandOperation operation,
        string bagBeforeExecution,
        CancellationToken cancellationToken)
    {
        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                bagBeforeExecution,
                _character!.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "HolyStoneKitBagDeleteAck");
        }
        await SendHolyStoneAuthoritativeProjectionAsync(
            reason,
            cancellationToken);
        if (operation == HolyStoneCommandOperation.Combine)
        {
            await SendHolyStoneCombinationResultPanelAsync(
                npcId,
                dialogIndex,
                nativeResultSubId,
                cancellationToken);
        }
        else if (operation ==
                 HolyStoneCommandOperation.ImplementSpirit)
        {
            await SendHolySpiritImplementationResultPanelAsync(
                npcId,
                dialogIndex,
                nativeResultSubId,
                cancellationToken);
        }
        else if (operation == HolyStoneCommandOperation.Upgrade)
        {
            await SendHolyStoneUpgradeResultPanelAsync(
                npcId,
                dialogIndex,
                nativeResultSubId,
                cancellationToken);
        }
        else
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    nativeResultSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
        }
    }

    private Task SendRawHolyStoneWrongSelectionAsync(
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken) =>
        _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneNativeResults.WrongSelectionSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

    private void RecordRawHolyStoneProviderUnavailable(
        CommandFamily family,
        Guid operationId,
        string reason)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ServerOperationId,
            CommandOutcome.ProviderUnavailable);
        Console.Error.WriteLine(
            "[holy-stone] raw durable operation unavailable " +
            $"account={_account?.Id.ToString() ?? "<none>"} " +
            $"character={_character?.Name ?? "<none>"} " +
            $"family={family} operation={operationId} reason={reason}");
    }
}
