using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool>
        TryReplayClassSuitBeforeRouteRejectionAsync(
            GamePacket packet,
            int wireSubId,
            CancellationToken cancellationToken)
    {
        if (!_session.IsSecure ||
            !packet.ClientOperationId.HasValue ||
            _account is null ||
            _character is null ||
            !TryMapClassSuitOperation(
                wireSubId,
                out var operation) ||
            !ClassSuitProtocol.TryReadMutation(
                packet,
                out var exactNpcId,
                out var wireIntent) ||
            !TryMapClassSuitOperation(
                wireIntent.Operation,
                out var parsedOperation) ||
            parsedOperation != operation ||
            !ClassSuitReplayIntent.TryCreate(
                operation,
                checked((int)exactNpcId),
                ClassSuitProtocol.DialogIndex,
                wireIntent.EquipmentLocation,
                wireIntent.EquipmentKitBagSlot,
                wireIntent.MaterialKitBagSlot,
                wireIntent.SecondaryMaterialKitBagSlot,
                out var replayIntent))
        {
            return false;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return true;
        }
        if (_classSuitCommands is null)
        {
            CommandMetrics.Record(
                ClassSuitCommandEnvelope.Family(operation),
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            return true;
        }

        var subject = new CommandSubject(_account.Id, _character.Id);
        var identity = ClassSuitOperationIdentity.SecureClient(
            packet.ClientOperationId.Value);
        try
        {
            var execution = await _classSuitCommands.TryReplayAsync(
                subject,
                ownership,
                replayIntent,
                identity,
                cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                return true;
            }
            if (execution.Disposition ==
                ClassSuitExecutionDisposition.ReplayNotFound)
            {
                return false;
            }
            if (!execution.IsDurable)
            {
                CommandMetrics.Record(
                    ClassSuitCommandEnvelope.Family(operation),
                    CommandIdentityStrength.ClientOperationId,
                    MapClassSuitOutcome(execution.Disposition));
                await SendClassSuitNonDurableAsync(
                    exactNpcId,
                    operation,
                    identity,
                    execution.Disposition,
                    cancellationToken);
                return true;
            }

            await CompleteUnroutedClassSuitReplayAsync(
                packet.ClientOperationId.Value,
                replayIntent,
                execution.Receipt!,
                ownership,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return true;
        }
        catch (Exception exception)
        {
            CommandMetrics.Record(
                ClassSuitCommandEnvelope.Family(operation),
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[class-suit] pre-route replay remains pending: " +
                exception.Message);
            return true;
        }
    }

    private async Task CompleteUnroutedClassSuitReplayAsync(
        Guid clientOperationId,
        ClassSuitReplayIntent expectedIntent,
        ClassSuitExecutionReceipt receipt,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (receipt.CharacterId != _character!.Id ||
            receipt.Family !=
                ClassSuitCommandEnvelope.Family(receipt.Operation) ||
            receipt.ReplayIntent != expectedIntent ||
            receipt.Operation != expectedIntent.Operation ||
            receipt.NpcId != expectedIntent.NpcId ||
            receipt.DialogIndex != expectedIntent.DialogIndex)
        {
            throw new InvalidDataException(
                "The Class Suit replay receipt identity is inconsistent.");
        }

        var bagBefore = _character.KitBag;
        await ReloadDurableClassSuitProjectionAsync(
            ownership,
            receipt,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        CommandMetrics.Record(
            receipt.Family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.Duplicate);
        await _session.SendAsync(
            ClassSuitProtocol.BuildResultResponse(
                checked((uint)expectedIntent.NpcId),
                receipt.NativeResultSubId),
            cancellationToken,
            "ClassSuitReplayResult");
        if (receipt.Status == ClassSuitCommandResultStatus.Succeeded)
        {
            foreach (var acknowledgement in
                PacketBuilder.KitBagMutationDeletionAcknowledgements(
                    bagBefore,
                    _character.KitBag))
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "ClassSuitReplayKitBagDeleteAck");
            }
        }
        await SendClassSuitAuthoritativeProjectionAsync(
            receipt,
            "replay",
            cancellationToken);
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            receipt.Family,
            receipt.NativeResultSubId,
            SecureLegacyCommandDisposition.Replayed,
            receipt.InventoryRevision,
            cancellationToken);
    }

    private static CommandFamily? ResolveSecureClassSuitCommandFamily(
        int wireSubId) =>
        TryMapClassSuitOperation(wireSubId, out var operation)
            ? ClassSuitCommandEnvelope.Family(operation)
            : null;
}
