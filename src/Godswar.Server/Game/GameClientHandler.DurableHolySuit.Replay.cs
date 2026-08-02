using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryResolveSecureHolySuitOutsideRouteAsync(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        int subId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!_session.IsSecure ||
            !packet.ClientOperationId.HasValue ||
            !HolySuitDesignProtocol.IsEndpoint(npcId, dialogIndex) ||
            !HolySuitDesignProtocol.TryResolveOperation(
                subId,
                out var wireOperation))
        {
            return false;
        }
        if (_account is null || _character is null)
        {
            return true;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return true;
        }

        var operation = ToCommandOperation(wireOperation);
        var identity = HolySuitOperationIdentity.SecureClient(
            packet.ClientOperationId.Value);
        if (_holySuitCommands is null)
        {
            RecordHolySuitProviderUnavailable(
                operation,
                $"{reason}; replay provider is not configured");
            return true;
        }

        HolySuitExecutionResult execution;
        try
        {
            execution = await _holySuitCommands.TryReplayAsync(
                new CommandSubject(_account.Id, _character.Id),
                ownership,
                operation,
                identity,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            RecordHolySuitMetric(
                operation,
                identity.Strength,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return true;
        }
        catch (Exception exception)
        {
            RecordHolySuitProviderUnavailable(
                operation,
                $"{reason}; replay failed: {exception.Message}");
            return true;
        }

        if (execution.IsDurable)
        {
            var receipt = execution.Receipt ??
                throw new InvalidDataException(
                    "A replayed Holy Suit result has no receipt.");
            var kitBagBefore = _character.KitBag;
            HolySuitProjectionRevisions projection;
            try
            {
                ValidateHolySuitReceipt(
                    npcId,
                    dialogIndex,
                    operation,
                    receipt);
                projection = await ReloadDurableHolySuitProjectionAsync(
                    ownership,
                    receipt,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                RecordHolySuitMetric(
                    operation,
                    identity.Strength,
                    CommandOutcome.Cancelled);
                throw;
            }
            catch (Exception exception)
            {
                RecordHolySuitProviderUnavailable(
                    operation,
                    $"{reason}; projection failed: " +
                    exception.Message);
                return true;
            }
            RecordHolySuitMetric(
                operation,
                identity.Strength,
                MapHolySuitOutcome(execution.Disposition));
            await SendDurableHolySuitReceiptAsync(
                npcId,
                identity,
                receipt,
                execution.Disposition,
                projection.InventoryRevision,
                kitBagBefore,
                cancellationToken);
            return true;
        }

        if (execution.Disposition ==
            HolySuitExecutionDisposition.ReplayNotFound)
        {
            var nativeResult = operation is
                HolySuitCommandOperation.StoreExperience or
                HolySuitCommandOperation.TransformExperience
                    ? HolySuitNativeResults.WrongSelectionSubId
                    : HolySuitNativeResults.TransferWrongSelectionSubId;
            RecordHolySuitMetric(
                operation,
                identity.Strength,
                CommandOutcome.PreconditionFailed);
            await _session.SendAsync(
                HolySuitDesignProtocol.BuildResultResponse(
                    npcId,
                    nativeResult),
                cancellationToken,
                "HolySuitWrongRoute");
            await SendSecureHolySuitResultAsync(
                identity.OperationId,
                operation,
                nativeResult,
                SecureLegacyCommandDisposition.Rejected,
                inventoryRevision: 0,
                cancellationToken);
            Console.WriteLine(
                "[holy-suit] rejected command outside authoritative " +
                $"NPC route reason={reason}");
            return true;
        }

        RecordHolySuitProviderUnavailable(
            operation,
            $"{reason}; unresolved disposition={execution.Disposition}");
        return true;
    }
}
