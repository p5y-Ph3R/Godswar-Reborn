using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IGearMentorMaterialConversionCommandExecutor?
        _gearMentorMaterialConversionCommands;

    private async Task
        HandleDurableGearMentorMaterialConversionAsync(
            GearMentorOperation operation,
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

        var family = MaterialConversionFamily(operation);
        var invalidResultSubId = MaterialConversionInvalidResultSubId(
            family);

        // The snapshot above is the only request-local state that may cross
        // the persistence await. Consume the NPC/page context first so a
        // second packet cannot reuse it while this command is unresolved.
        ClearGearEnhancerSelection();

        if (_gearMentorMaterialConversionCommands is null)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            // This UUID may describe a transaction committed before a
            // reconnect to a misconfigured instance. Without the durable
            // inbox provider, the server cannot distinguish that replay from
            // a new request. Do not settle it with a false rejection.
            Console.Error.WriteLine(
                "[gear-mentor] durable material conversion unavailable " +
                $"account={_account.Id} character={_character.Name} " +
                $"operation={operation}; operation remains pending");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        GearMentorMaterialConversionExecutionResult execution;
        try
        {
            execution = await ExecuteMaterialConversionAsync(
                operation,
                subject,
                npcId,
                clientOperationId,
                selection,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            // The database commit may have succeeded. Do not settle the
            // native UUID until a retry can resolve the permanent inbox.
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-mentor] durable material conversion provider " +
                $"failure account={_account.Id} " +
                $"character={_character.Name} operation={operation}: " +
                ex.Message);
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            MapMaterialConversionCommandOutcome(execution.Disposition));

        if (!execution.IsDurable)
        {
            var disposition =
                execution.Disposition ==
                    GearMentorMaterialConversionExecutionDisposition
                        .RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected;
            await SendMaterialConversionTerminalAsync(
                npcId,
                clientOperationId,
                family,
                invalidResultSubId,
                disposition,
                inventoryRevision: 0,
                cancellationToken);
            Console.WriteLine(
                "[gear-mentor] durable material conversion rejected " +
                $"account={_account.Id} character={_character.Name} " +
                $"operation={operation} outcome={execution.Disposition} " +
                $"selections=({selectionSummary})");
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable material conversion result has no receipt.");
        if (receipt.Family != family ||
            receipt.CharacterId != _character.Id)
        {
            throw new InvalidDataException(
                "The durable material conversion receipt identity does " +
                "not match the active command.");
        }

        await ReloadDurableInventoryProjectionAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                GearEnhancerProtocol.DialogIndex,
                receipt.NativeResultSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (receipt.Status ==
            GearMentorMaterialConversionResultStatus.Succeeded)
        {
            foreach (var acknowledgement in
                PacketBuilder.KitBagMutationDeletionAcknowledgements(
                    kitBagBeforeTransaction,
                    _character!.KitBag))
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "GearMentorKitBagDeleteAck");
            }
        }

        // Every durable rejection refreshes too: the selected material may
        // have changed on another request or before this retry arrived.
        await SendKitBagRefreshAsync(cancellationToken);

        var resultDisposition = execution.Disposition switch
        {
            GearMentorMaterialConversionExecutionDisposition.Committed =>
                SecureLegacyCommandDisposition.Applied,
            GearMentorMaterialConversionExecutionDisposition.Duplicate =>
                SecureLegacyCommandDisposition.Replayed,
            _ => SecureLegacyCommandDisposition.Rejected
        };
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            family,
            receipt.NativeResultSubId,
            resultDisposition,
            receipt.InventoryRevision,
            cancellationToken);
        Console.WriteLine(
            "[gear-mentor] durable material conversion completed " +
            $"account={_account.Id} character={_character!.Name} " +
            $"operation={operation} status={receipt.Status} " +
            $"outcome={execution.Disposition} " +
            $"revision={receipt.InventoryRevision} " +
            $"source={receipt.SourceItemId} " +
            $"output={receipt.OutputItemId}x{receipt.OutputQuantity} " +
            $"selections=({selectionSummary})");
    }

    private async Task<GearMentorMaterialConversionExecutionResult>
        ExecuteMaterialConversionAsync(
            GearMentorOperation operation,
            CommandSubject subject,
            uint npcId,
            Guid clientOperationId,
            GearEnhancerSelectionSnapshot? selection,
            CancellationToken cancellationToken)
    {
        if (!selection.HasValue)
        {
            return operation switch
            {
                GearMentorOperation.TransformCrystal =>
                    await _gearMentorMaterialConversionCommands!
                        .TryReplayTransformAsync(
                            subject,
                            clientOperationId,
                            cancellationToken),
                GearMentorOperation.CombineGemPieces =>
                    await _gearMentorMaterialConversionCommands!
                        .TryReplayCombineAsync(
                            subject,
                            clientOperationId,
                            cancellationToken),
                _ => GearMentorMaterialConversionExecutionResult
                    .InvalidIntent()
            };
        }

        var selected = selection.Value;
        var expectedState = selected.ExpectedItem.ToCompactString();
        var correlation = new CommandConnectionCorrelation(
            _commandConnectionId,
            CommandTransportKind.SecureTlsLegacy);
        var receivedAt = DateTimeOffset.UtcNow;
        switch (operation)
        {
            case GearMentorOperation.TransformCrystal:
                if (!GearMentorTransformCrystalCommandEnvelope
                    .TryCreateCommand(
                        clientOperationId,
                        checked((int)npcId),
                        selected.KitBagSlot,
                        expectedState,
                        out var transform))
                {
                    return GearMentorMaterialConversionExecutionResult
                        .InvalidIntent();
                }

                return await _gearMentorMaterialConversionCommands!
                    .ExecuteAsync(
                        GearMentorTransformCrystalCommandEnvelope.Create(
                            subject,
                            correlation,
                            receivedAt,
                            transform),
                        cancellationToken);

            case GearMentorOperation.CombineGemPieces:
                if (!GearMentorCombineGemPiecesCommandEnvelope
                    .TryCreateCommand(
                        clientOperationId,
                        checked((int)npcId),
                        selected.KitBagSlot,
                        expectedState,
                        out var combine))
                {
                    return GearMentorMaterialConversionExecutionResult
                        .InvalidIntent();
                }

                return await _gearMentorMaterialConversionCommands!
                    .ExecuteAsync(
                        GearMentorCombineGemPiecesCommandEnvelope.Create(
                            subject,
                            correlation,
                            receivedAt,
                            combine),
                        cancellationToken);

            default:
                return GearMentorMaterialConversionExecutionResult
                    .InvalidIntent();
        }
    }

    private async Task SendMaterialConversionTerminalAsync(
        uint npcId,
        Guid clientOperationId,
        CommandFamily family,
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
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            family,
            nativeResultSubId,
            disposition,
            inventoryRevision,
            cancellationToken);
    }

    private static CommandFamily MaterialConversionFamily(
        GearMentorOperation operation) =>
        operation switch
        {
            GearMentorOperation.TransformCrystal =>
                CommandFamily.GearMentorTransformCrystal,
            GearMentorOperation.CombineGemPieces =>
                CommandFamily.GearMentorCombineGemPieces,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static int MaterialConversionInvalidResultSubId(
        CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorTransformCrystal =>
                GearMentorMaterialConversionNativeResults
                    .TransformInvalidCrystalSubId,
            CommandFamily.GearMentorCombineGemPieces =>
                GearMentorMaterialConversionNativeResults
                    .CombineInvalidGemPiecesSubId,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    private static CommandOutcome MapMaterialConversionCommandOutcome(
        GearMentorMaterialConversionExecutionDisposition disposition) =>
        disposition switch
        {
            GearMentorMaterialConversionExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            GearMentorMaterialConversionExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            GearMentorMaterialConversionExecutionDisposition
                .RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            GearMentorMaterialConversionExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            _ => CommandOutcome.PreconditionFailed
        };
}
