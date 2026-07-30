using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly IGearMentorDecomposeGearCommandExecutor?
        _gearMentorDecomposeGearCommands;

    private async Task HandleDurableGearMentorDecomposeAsync(
        uint npcId,
        Guid clientOperationId,
        IReadOnlyList<GearEnhancerSelectionSnapshot>? selections,
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

        // Nothing except this immutable request snapshot may cross the
        // persistence await. Consuming the page context also prevents a
        // second final-action packet from reusing the same selections.
        ClearGearEnhancerSelection();

        if (_gearMentorDecomposeGearCommands is null)
        {
            CommandMetrics.Record(
                CommandFamily.GearMentorDecomposeGear,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-mentor] durable Decompose unavailable " +
                $"account={_account.Id} character={_character.Name}; " +
                "operation remains pending");
            return;
        }

        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        GearMentorDecomposeGearExecutionResult execution;
        try
        {
            execution = await ExecuteDecomposeAsync(
                subject,
                npcId,
                clientOperationId,
                selections,
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                CommandFamily.GearMentorDecomposeGear,
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
            // The commit may already be permanent even though its response
            // was lost. Leave the UUID unsettled until a retry can inspect
            // the durable inbox.
            CommandMetrics.Record(
                CommandFamily.GearMentorDecomposeGear,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-mentor] durable Decompose provider failure " +
                $"account={_account.Id} character={_character.Name}: " +
                ex.Message);
            return;
        }

        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        CommandMetrics.Record(
            CommandFamily.GearMentorDecomposeGear,
            CommandIdentityStrength.ClientOperationId,
            MapDecomposeCommandOutcome(execution.Disposition));

        if (!execution.IsDurable)
        {
            var disposition =
                execution.Disposition ==
                    GearMentorDecomposeGearExecutionDisposition
                        .RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected;
            await SendDecomposeTerminalAsync(
                npcId,
                clientOperationId,
                GearMentorDecomposeGearNativeResults.SelectionMissingSubId,
                disposition,
                inventoryRevision: 0,
                cancellationToken);
            Console.WriteLine(
                "[gear-mentor] durable Decompose rejected " +
                $"account={_account.Id} character={_character.Name} " +
                $"outcome={execution.Disposition} " +
                $"selections=({selectionSummary})");
            return;
        }

        var receipt = execution.Receipt ??
            throw new InvalidDataException(
                "A durable Decompose result has no receipt.");
        if (receipt.Family != CommandFamily.GearMentorDecomposeGear ||
            receipt.CharacterId != _character.Id)
        {
            throw new InvalidDataException(
                "The durable Decompose receipt identity does not match " +
                "the active command.");
        }

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
            GearMentorDecomposeGearResultStatus.Succeeded)
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

        // Rejections refresh too because an item can become stale before a
        // retry, while successful results must reveal the exact stored Dust.
        await SendKitBagRefreshAsync(cancellationToken);

        var resultDisposition = execution.Disposition switch
        {
            GearMentorDecomposeGearExecutionDisposition.Committed =>
                SecureLegacyCommandDisposition.Applied,
            GearMentorDecomposeGearExecutionDisposition.Duplicate =>
                SecureLegacyCommandDisposition.Replayed,
            _ => SecureLegacyCommandDisposition.Rejected
        };
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            CommandFamily.GearMentorDecomposeGear,
            receipt.NativeResultSubId,
            resultDisposition,
            receipt.InventoryRevision,
            cancellationToken);

        var outcomes = receipt.DustOutcomes.IsEmpty
            ? "none"
            : string.Join(
                ',',
                receipt.DustOutcomes.Select(static outcome =>
                    $"slot={outcome.SelectedKitBagSlot}:" +
                    $"{outcome.DustItemId}x{outcome.Quantity}:" +
                    $"bound={outcome.Bound}"));
        Console.WriteLine(
            "[gear-mentor] durable Decompose completed " +
            $"account={_account.Id} character={_character!.Name} " +
            $"status={receipt.Status} outcome={execution.Disposition} " +
            $"revision={receipt.InventoryRevision} dust=({outcomes}) " +
            $"selections=({selectionSummary})");
    }

    private async Task<GearMentorDecomposeGearExecutionResult>
        ExecuteDecomposeAsync(
            CommandSubject subject,
            uint npcId,
            Guid clientOperationId,
            IReadOnlyList<GearEnhancerSelectionSnapshot>? selections,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken)
    {
        if (selections is null)
        {
            return await _gearMentorDecomposeGearCommands!.TryReplayAsync(
                subject,
                ownership,
                clientOperationId,
                cancellationToken);
        }

        var commandSelections = selections
            .Select(static selection =>
                new GearMentorDecomposeSelection(
                    selection.KitBagSlot,
                    selection.ExpectedItem.ToCompactString()))
            .ToArray();
        if (!GearMentorDecomposeGearCommandEnvelope.TryCreateCommand(
                clientOperationId,
                checked((int)npcId),
                commandSelections,
                out var command))
        {
            return GearMentorDecomposeGearExecutionResult.InvalidIntent();
        }

        var envelope = GearMentorDecomposeGearCommandEnvelope.Create(
            subject,
            new CommandConnectionCorrelation(
                _commandConnectionId,
                CommandTransportKind.SecureTlsLegacy),
            DateTimeOffset.UtcNow,
            command) with
        {
            Ownership = ownership
        };
        return await _gearMentorDecomposeGearCommands!.ExecuteAsync(
            envelope,
            cancellationToken);
    }

    private async Task SendDecomposeTerminalAsync(
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
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            CommandFamily.GearMentorDecomposeGear,
            nativeResultSubId,
            disposition,
            inventoryRevision,
            cancellationToken);
    }

    private static CommandOutcome MapDecomposeCommandOutcome(
        GearMentorDecomposeGearExecutionDisposition disposition) =>
        disposition switch
        {
            GearMentorDecomposeGearExecutionDisposition.Committed =>
                CommandOutcome.Accepted,
            GearMentorDecomposeGearExecutionDisposition.Duplicate =>
                CommandOutcome.Duplicate,
            GearMentorDecomposeGearExecutionDisposition
                .RequestHashConflict =>
                CommandOutcome.RequestHashConflict,
            GearMentorDecomposeGearExecutionDisposition.InvalidIntent =>
                CommandOutcome.InvalidIntent,
            _ => CommandOutcome.PreconditionFailed
        };
}
