using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IMakeAttributeStoneCommandExecutor?
        _makeAttributeStoneCommands;

    private async Task<bool> TryRejectUnroutedSecureCommandAsync(
        GamePacket packet,
        uint? npcId,
        string reason,
        CancellationToken cancellationToken,
        CommandFamily? commandFamily = null,
        int? responseDialogIndex = null)
    {
        if (!packet.ClientOperationId.HasValue ||
            !_session.IsSecure)
        {
            return false;
        }

        if (!commandFamily.HasValue)
        {
            // The v1 operation marker deliberately carries only the UUID and
            // legacy packet boundary. If the legacy body cannot establish an
            // exact family, guessing would send a contradictory 0x0102 and
            // make the native retry registry fail closed. Keep the operation
            // pending so a later authenticated retry can resolve it.
            Console.WriteLine(
                "[gear-mentor] preserved unrouted secure command " +
                $"reason={reason} family=unknown");
            return true;
        }

        CommandMetrics.Record(
            commandFamily.Value,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.PreconditionFailed);
        var nativeResultSubId = commandFamily.Value switch
        {
            CommandFamily.GearMentorMakeAttributeStone =>
                GearEnhancerProtocol.SelectedItemMissingResultSubId,
            CommandFamily.GearMentorDecomposeGear =>
                GearMentorDecomposeGearNativeResults
                    .SelectionMissingSubId,
            CommandFamily.GearMentorEnhanceAttribute or
                CommandFamily.GearMentorAddAttribute or
                CommandFamily.GearMentorDeleteAttribute =>
                GearEnhancerProtocol.InvalidSelectionResultSubId,
            CommandFamily.GearMentorTransformCrystal or
                CommandFamily.GearMentorCombineGemPieces =>
                MaterialConversionInvalidResultSubId(
                    commandFamily.Value),
            CommandFamily.ClassSuitExchangeTierI or
                CommandFamily.ClassSuitConvertToCommon or
                CommandFamily.ClassSuitUpgradeTierII or
                CommandFamily.ClassSuitUpgradeTierIII or
                CommandFamily.ClassSuitUpgradeTierIV or
                CommandFamily.ClassSuitAddAttribute or
                CommandFamily.ClassSuitDeleteAttribute =>
                ClassSuitNativeResults.GenericWrongSelection,
            _ => throw new ArgumentOutOfRangeException(
                nameof(commandFamily))
        };
        if (npcId.HasValue)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId.Value,
                    responseDialogIndex ??
                        GearEnhancerProtocol.DialogIndex,
                    nativeResultSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
        }

        await SendSecureGearMentorResultAsync(
            packet.ClientOperationId.Value,
            commandFamily.Value,
            nativeResultSubId,
            SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
        Console.WriteLine(
            "[gear-mentor] rejected unrouted secure command " +
            $"reason={reason}");
        return true;
    }

    private async Task HandleDurableMakeAttributeStoneAsync(
        uint npcId,
        Guid clientOperationId,
        GearEnhancerSelectionSnapshot? selection,
        string kitBagBeforeTransaction,
        string selectionSummary,
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

        if (_makeAttributeStoneCommands is null)
        {
            ClearGearEnhancerSelection();
            CommandMetrics.Record(
                CommandFamily.GearMentorMakeAttributeStone,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            // A prior instance may already have committed this UUID. Without
            // its inbox provider, this instance cannot truthfully reject it.
            Console.Error.WriteLine(
                "[gear-mentor] durable Make Attribute Stone unavailable " +
                $"account={_account.Id} character={_character.Name}; " +
                "operation remains pending");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        MakeAttributeStoneExecutionResult execution;
        CommandEnvelope<GearMentorMakeAttributeStoneCommand>?
            envelope = null;
        try
        {
            if (!selection.HasValue)
            {
                // A retry after reconnect has no trustworthy ephemeral NPC
                // selection. Resolve the durable operation identity first;
                // only a miss is allowed to become "selected item missing".
                execution =
                    await _makeAttributeStoneCommands.TryReplayAsync(
                        subject,
                        ownership,
                        clientOperationId,
                        cancellationToken);
            }
            else if (!GearMentorMakeAttributeStoneCommandEnvelope
                .TryCreateCommand(
                    clientOperationId,
                    checked((int)npcId),
                    selection.Value.KitBagSlot,
                    selection.Value.ExpectedItem.ToCompactString(),
                    out var command))
            {
                execution =
                    MakeAttributeStoneExecutionResult.InvalidIntent();
            }
            else
            {
                envelope =
                    GearMentorMakeAttributeStoneCommandEnvelope.Create(
                        subject,
                        new CommandConnectionCorrelation(
                            _commandConnectionId,
                            CommandTransportKind.SecureTlsLegacy),
                        DateTimeOffset.UtcNow,
                        command) with
                    {
                        Ownership = ownership
                    };
                execution =
                    await _makeAttributeStoneCommands.ExecuteAsync(
                        envelope,
                        cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                CommandFamily.GearMentorMakeAttributeStone,
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
            // The commit outcome is unknown. Do not send a terminal secure
            // result: retaining the client operation ID is what makes a later
            // retry safe after a lost acknowledgement.
            CommandMetrics.Record(
                CommandFamily.GearMentorMakeAttributeStone,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-mentor] durable Make Attribute Stone provider " +
                $"failure account={_account.Id} " +
                $"character={_character.Name}: {ex.Message}");
            ClearGearEnhancerSelection();
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        ClearGearEnhancerSelection();
        var commandOutcome = MapCommandOutcome(execution.Disposition);
        CommandMetrics.Record(
            envelope?.Family ??
                CommandFamily.GearMentorMakeAttributeStone,
            CommandIdentityStrength.ClientOperationId,
            commandOutcome);

        if (!execution.IsDurable)
        {
            var disposition =
                execution.Disposition ==
                    MakeAttributeStoneExecutionDisposition
                        .RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected;
            await SendMakeAttributeStoneTerminalAsync(
                npcId,
                clientOperationId,
                GearEnhancerProtocol.SelectedItemMissingResultSubId,
                disposition,
                inventoryRevision: 0,
                cancellationToken);
            Console.WriteLine(
                "[gear-mentor] durable Make Attribute Stone rejected " +
                $"account={_account.Id} character={_character.Name} " +
                $"outcome={execution.Disposition} " +
                $"selections=({selectionSummary})");
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Make Attribute Stone result has no receipt.");
        await ReloadDurableInventoryProjectionAsync(
            ownership,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                GearEnhancerProtocol.DialogIndex,
                receipt.NativeResultSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (receipt.Status ==
            MakeAttributeStoneResultStatus.Succeeded)
        {
            foreach (var acknowledgement in
                PacketBuilder
                    .KitBagMutationDeletionAcknowledgements(
                        kitBagBeforeTransaction,
                        _character!.KitBag))
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "GearMentorKitBagDeleteAck");
            }
        }

        await SendKitBagRefreshAsync(cancellationToken);

        var resultDisposition = execution.Disposition switch
        {
            MakeAttributeStoneExecutionDisposition.Committed =>
                SecureLegacyCommandDisposition.Applied,
            MakeAttributeStoneExecutionDisposition.Duplicate =>
                SecureLegacyCommandDisposition.Replayed,
            _ => SecureLegacyCommandDisposition.Rejected
        };
        await SendSecureMakeAttributeStoneResultAsync(
            clientOperationId,
            receipt.NativeResultSubId,
            resultDisposition,
            receipt.InventoryRevision,
            cancellationToken);
        Console.WriteLine(
            "[gear-mentor] durable Make Attribute Stone completed " +
            $"account={_account.Id} character={_character!.Name} " +
            $"status={receipt.Status} outcome={execution.Disposition} " +
            $"revision={receipt.InventoryRevision} " +
            $"selections=({selectionSummary})");
    }

    private async Task ReloadDurableInventoryProjectionAsync(
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The inventory owner changed during projection reload.");
        }

        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(accountSnapshot);
        if (hydrated is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable inventory character could not be reloaded.");
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
    }

    private async Task SendMakeAttributeStoneTerminalAsync(
        uint npcId,
        Guid clientOperationId,
        int nativeResultSubId,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                GearEnhancerProtocol.DialogIndex,
                nativeResultSubId),
            cancellationToken,
            "NpcFunctionActionResponse");
        await SendSecureMakeAttributeStoneResultAsync(
            clientOperationId,
            nativeResultSubId,
            disposition,
            inventoryRevision,
            cancellationToken);
    }

    private ValueTask SendSecureMakeAttributeStoneResultAsync(
        Guid clientOperationId,
        int nativeResultSubId,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Client operation identity requires the secure " +
                "transport.");
        }

        return SendSecureGearMentorResultAsync(
            clientOperationId,
            CommandFamily.GearMentorMakeAttributeStone,
            nativeResultSubId,
            disposition,
            inventoryRevision,
            cancellationToken);
    }

    private ValueTask SendSecureGearMentorResultAsync(
        Guid clientOperationId,
        CommandFamily commandFamily,
        int nativeResultSubId,
        SecureLegacyCommandDisposition disposition,
        long inventoryRevision,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure)
        {
            throw new InvalidOperationException(
                "Client operation identity requires the secure " +
                "transport.");
        }

        return _session.SendLegacyCommandResultAsync(
            new SecureLegacyCommandResult(
                disposition,
                (ushort)commandFamily,
                checked((uint)nativeResultSubId),
                checked((ulong)inventoryRevision),
                clientOperationId),
            cancellationToken);
    }

    private static CommandOutcome MapCommandOutcome(
        MakeAttributeStoneExecutionDisposition disposition) =>
        disposition switch
        {
            MakeAttributeStoneExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            MakeAttributeStoneExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            MakeAttributeStoneExecutionDisposition
                .RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            MakeAttributeStoneExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            _ => CommandOutcome.PreconditionFailed
        };
}
