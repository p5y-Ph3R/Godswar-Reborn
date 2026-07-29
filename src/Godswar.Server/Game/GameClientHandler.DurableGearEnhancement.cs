using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleDurableGearEnhancementAsync(
        uint npcId,
        int dialogIndex,
        GearEnhancementOperation operation,
        Guid clientOperationId,
        GearEnhancerSelectionTriplet? selections,
        string kitBagBeforeTransaction,
        string selectionSummary,
        CancellationToken cancellationToken)
    {
        if (_account is null || _character is null)
        {
            return;
        }

        var commandOperation = ToCommandOperation(operation);
        var family =
            GearEnhancementCommandEnvelope.Family(commandOperation);
        if (_gearEnhancementCommands is null)
        {
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-enhancement] durable provider unavailable " +
                $"account={_account.Id} character={_character.Name} " +
                $"family={family}; operation remains pending");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        GearEnhancementExecutionResult execution;
        try
        {
            execution = await _gearEnhancementCommands.TryReplayAsync(
                subject,
                commandOperation,
                clientOperationId,
                cancellationToken);
            if (selections.HasValue &&
                execution.Disposition ==
                    GearEnhancementExecutionDisposition.ReplayNotFound)
            {
                // Reconnect can rebuild the same UI from already-mutated
                // authoritative items. Resolve the UUID before hashing those
                // new snapshots, otherwise a committed retry would be
                // misreported as a request conflict.
                execution = await ExecuteGearEnhancementAsync(
                    subject,
                    npcId,
                    dialogIndex,
                    commandOperation,
                    clientOperationId,
                    selections.Value,
                    cancellationToken);
            }
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
            // The provider may have committed immediately before the
            // connection failed. A terminal reply here would discard the
            // operation UUID and make the outcome impossible to recover.
            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-enhancement] durable provider failure " +
                $"account={_account.Id} character={_character.Name} " +
                $"family={family}: {ex.Message}");
            return;
        }

        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            MapGearEnhancementOutcome(execution.Disposition));
        if (!execution.IsDurable)
        {
            if (execution.Disposition ==
                GearEnhancementExecutionDisposition.ReplayNotFound)
            {
                // A final-action retry contains only its UUID. Without the
                // three immutable item snapshots, a replay miss cannot safely
                // become a new mutation or a false terminal rejection.
                Console.WriteLine(
                    "[gear-enhancement] durable replay not found; " +
                    $"operation remains pending account={_account.Id} " +
                    $"character={_character.Name} family={family}");
                return;
            }

            var disposition =
                execution.Disposition ==
                    GearEnhancementExecutionDisposition
                        .RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected;
            await SendGearEnhancementTerminalAsync(
                npcId,
                dialogIndex,
                clientOperationId,
                family,
                GearEnhancementNativeResults.InvalidSelectionSubId,
                disposition,
                inventoryRevision: 0,
                cancellationToken);
            Console.WriteLine(
                "[gear-enhancement] durable command rejected before " +
                $"receipt account={_account.Id} " +
                $"character={_character.Name} family={family} " +
                $"outcome={execution.Disposition}");
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Gear Enhancement outcome has no receipt.");
        if (receipt.CharacterId != _character.Id ||
            receipt.Operation != commandOperation ||
            receipt.Family != family ||
            receipt.NpcId != checked((int)npcId) ||
            receipt.DialogIndex != dialogIndex)
        {
            throw new InvalidDataException(
                "The Gear Enhancement receipt identity does not match " +
                "the active command.");
        }

        await ReloadDurableInventoryProjectionAsync(cancellationToken);
        await SendDurableGearEnhancementReceiptAsync(
            clientOperationId,
            receipt,
            execution.Disposition,
            kitBagBeforeTransaction,
            cancellationToken);
        Console.WriteLine(
            "[gear-enhancement] durable command completed " +
            $"account={_account.Id} character={_character!.Name} " +
            $"family={family} status={receipt.Status} " +
            $"outcome={execution.Disposition} " +
            $"revision={receipt.InventoryRevision} " +
            $"selections=({selectionSummary})");
    }

    private async Task<GearEnhancementExecutionResult>
        ExecuteGearEnhancementAsync(
            CommandSubject subject,
            uint npcId,
            int dialogIndex,
            GearEnhancementCommandOperation operation,
            Guid clientOperationId,
            GearEnhancerSelectionTriplet selections,
            CancellationToken cancellationToken)
    {
        if (!GearEnhancementCommandEnvelope.TryCreateCommand(
                clientOperationId,
                operation,
                checked((int)npcId),
                dialogIndex,
                CreateCommandSelection(
                    GearEnhancementCommandItemRole.Gear,
                    selections.Gear),
                CreateCommandSelection(
                    GearEnhancementCommandItemRole.Catalyst,
                    selections.Catalyst),
                CreateCommandSelection(
                    GearEnhancementCommandItemRole.AttributeStone,
                    selections.AttributeStone),
                out var command))
        {
            return GearEnhancementExecutionResult.InvalidIntent();
        }

        var envelope = GearEnhancementCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command);
        return await _gearEnhancementCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task SendDurableGearEnhancementReceiptAsync(
        Guid clientOperationId,
        GearEnhancementExecutionReceipt receipt,
        GearEnhancementExecutionDisposition executionDisposition,
        string kitBagBeforeTransaction,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                checked((uint)receipt.NpcId),
                receipt.DialogIndex,
                receipt.NativeResultSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (receipt.Status ==
            GearEnhancementCommandResultStatus.Succeeded)
        {
            foreach (var acknowledgement in
                PacketBuilder.KitBagMutationDeletionAcknowledgements(
                    kitBagBeforeTransaction,
                    _character!.KitBag))
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "GearEnhancementKitBagDeleteAck");
            }
        }

        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            receipt.Family,
            receipt.NativeResultSubId,
            executionDisposition switch
            {
                GearEnhancementExecutionDisposition.Committed =>
                    SecureLegacyCommandDisposition.Applied,
                GearEnhancementExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                _ => SecureLegacyCommandDisposition.Rejected
            },
            receipt.InventoryRevision,
            cancellationToken);
    }

    private async Task SendGearEnhancementTerminalAsync(
        uint npcId,
        int dialogIndex,
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
                dialogIndex,
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

    private static GearEnhancementCommandSelection
        CreateCommandSelection(
            GearEnhancementCommandItemRole role,
            GearEnhancerSelectionSnapshot selection) =>
        new(
            role,
            selection.KitBagSlot,
            selection.ExpectedItem.ToCompactString());

    private static GearEnhancementCommandOperation ToCommandOperation(
        GearEnhancementOperation operation) =>
        operation switch
        {
            GearEnhancementOperation.Enhance =>
                GearEnhancementCommandOperation.Enhance,
            GearEnhancementOperation.Add =>
                GearEnhancementCommandOperation.Add,
            GearEnhancementOperation.Delete =>
                GearEnhancementCommandOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static CommandOutcome MapGearEnhancementOutcome(
        GearEnhancementExecutionDisposition disposition) =>
        disposition switch
        {
            GearEnhancementExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            GearEnhancementExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            GearEnhancementExecutionDisposition.RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            GearEnhancementExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            GearEnhancementExecutionDisposition.ReplayNotFound or
                GearEnhancementExecutionDisposition.PreconditionFailed or
                GearEnhancementExecutionDisposition.TerminalRejected =>
                CommandOutcome.PreconditionFailed,
            _ => CommandOutcome.ProviderUnavailable
        };
}
