using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool>
        TryReplayDurableHolyStoneBeforeRouteRejectionAsync(
            GamePacket packet,
            uint npcId,
            int dialogIndex,
            HolyStoneWireIntent intent,
            CancellationToken cancellationToken)
    {
        if (!packet.ClientOperationId.HasValue ||
            !_session.IsSecure ||
            _account is null ||
            _character is null)
        {
            return false;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return true;
        }

        var family = HolyStoneProtocol.Family(intent.Operation);
        if (_holyStoneCommands is null)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                packet.ClientOperationId.Value,
                "pre-route replay provider is not configured");
            return true;
        }

        try
        {
            var execution = await _holyStoneCommands.TryReplayAsync(
                new CommandSubject(_account.Id, _character.Id),
                ownership,
                intent.Operation,
                packet.ClientOperationId.Value,
                cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return true;
            }

            if (execution.Disposition ==
                HolyStoneExecutionDisposition.ReplayNotFound)
            {
                return false;
            }
            if (!execution.IsDurable)
            {
                RecordHolyStoneProviderUnavailable(
                    family,
                    packet.ClientOperationId.Value,
                    $"pre-route replay unresolved: {execution.Disposition}");
                return true;
            }

            var receipt = execution.Receipt ??
                throw new InvalidDataException(
                    "A durable Holy Stone replay has no receipt.");
            var replayIntent = ResolveHolyStoneReplayIntent(
                intent,
                receipt);
            ValidateHolyStoneReceiptIdentity(
                _character.Id,
                npcId,
                dialogIndex,
                replayIntent,
                receipt);
            var kitBagBeforeReplay = _character.KitBag;
            await ReloadDurableHolyStoneProjectionAsync(
                committedReceipt: null,
                ownership,
                cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return true;
            }

            CommandMetrics.Record(
                family,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Duplicate);
            await SendDurableHolyStoneReceiptAsync(
                npcId,
                dialogIndex,
                packet.ClientOperationId.Value,
                receipt,
                HolyStoneExecutionDisposition.Duplicate,
                kitBagBeforeReplay,
                cancellationToken);
            Console.WriteLine(
                "[holy-stone] replayed durable outcome before route " +
                $"rejection account={_account.Id} " +
                $"character={_character.Name} family={family} " +
                $"revision={receipt.InventoryRevision}");
            return true;
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
            return true;
        }
        catch (Exception ex)
        {
            RecordHolyStoneProviderUnavailable(
                family,
                packet.ClientOperationId.Value,
                $"pre-route replay failed: {ex.Message}");
            return true;
        }
    }

    private static HolyStoneWireIntent ResolveHolyStoneReplayIntent(
        HolyStoneWireIntent intent,
        HolyStoneExecutionReceipt receipt)
    {
        if (intent.Operation is not (
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit))
        {
            return intent;
        }
        var hasCompleteIdentity =
            intent.TargetSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            intent.StoneKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            (intent.Operation is
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.ImplementSpirit ||
             intent.CatalystKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
             intent.ThirdMaterialKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot);
        if (hasCompleteIdentity)
        {
            return intent;
        }

        // Staged stock confirmations carry item identity only in their
        // preceding 10193 selection packets. Those selections are one-shot and
        // may no longer exist when the same secure UUID is retried after a lost
        // response. The durable receipt is the authoritative identity for that
        // UUID; a new UUID cannot enter this branch because it has no inbox row.
        var thirdMaterialSlot = receipt.Operation ==
                HolyStoneCommandOperation.Combine
            ? receipt.CombinationEvidence?.ThirdMaterialKitBagSlot ??
                HolyStoneCommandEnvelope.NoStoneKitBagSlot
            : HolyStoneCommandEnvelope.NoStoneKitBagSlot;
        return intent with
        {
            TargetLocation = receipt.TargetLocation,
            TargetSlot = receipt.TargetSlot,
            StoneKitBagSlot = receipt.StoneKitBagSlot,
            CatalystKitBagSlot = receipt.CatalystKitBagSlot,
            ThirdMaterialKitBagSlot = thirdMaterialSlot
        };
    }

    private async Task RejectMalformedSecureHolyStoneAsync(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        HolyStoneCommandOperation operation,
        string reason,
        CancellationToken cancellationToken)
    {
        var family = HolyStoneProtocol.Family(operation);
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.Malformed);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneNativeResults.WrongSelectionSubId),
            cancellationToken,
            "NpcFunctionActionResponse");
        await SendSecureHolyStoneResultAsync(
            packet.ClientOperationId!.Value,
            family,
            HolyStoneNativeResults.WrongSelectionSubId,
            SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
        Console.WriteLine(
            "[holy-stone] rejected malformed secure operation " +
            $"family={family} reason={reason}");
    }

    private async Task RejectUnidentifiedSecureHolyStoneAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        CancellationToken cancellationToken)
    {
        if (!HolyStoneProtocol.TryResolveBoundaryOperation(
                subId,
                out var operation))
        {
            throw new ArgumentOutOfRangeException(nameof(subId));
        }

        var family = HolyStoneProtocol.Family(operation);
        CommandMetrics.RecordUnsupportedLegacyIdentity(family);
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.UnsupportedLegacyRetry,
            CommandOutcome.InvalidIntent);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneNativeResults.WrongSelectionSubId),
            cancellationToken,
            "NpcFunctionActionResponse");
        Console.WriteLine(
            "[holy-stone] rejected secure mutation without operation UUID " +
            $"family={family}");
    }

    private async Task RejectUnroutedSecureHolyStoneAsync(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        string reason,
        CancellationToken cancellationToken)
    {
        var family = HolyStoneProtocol.Family(intent.Operation);
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.PreconditionFailed);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneNativeResults.WrongSelectionSubId),
            cancellationToken,
            "NpcFunctionActionResponse");
        await SendSecureHolyStoneResultAsync(
            packet.ClientOperationId!.Value,
            family,
            HolyStoneNativeResults.WrongSelectionSubId,
            SecureLegacyCommandDisposition.Rejected,
            inventoryRevision: 0,
            cancellationToken);
        Console.WriteLine(
            "[holy-stone] rejected unrouted secure operation " +
            $"family={family} reason={reason}");
    }
}
