using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendWarehouseManagerMenuAsync(
        NpcSpawnDefinition npc,
        NpcDialogueRouteDefinition route,
        CancellationToken cancellationToken)
    {
        if (_account is null ||
            _character is null ||
            _warehouseSnapshots is null ||
            _warehouseExpansionPolicy is null ||
            !TryCaptureCurrentPlayerOwnership(out var ownership) ||
            route.Behavior != NpcDialogueBehavior.WarehouseManager ||
            !WarehouseNpcProtocol.IsManagerEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            return;
        }

        WarehouseSnapshot? snapshot;
        try
        {
            snapshot = await _warehouseSnapshots.ReadAsync(
                new CommandSubject(_account.Id, _character.Id),
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                "[warehouse-manager] menu snapshot unavailable: " +
                exception.Message);
            return;
        }
        if (snapshot is null ||
            !RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }
        snapshot.Validate();
        var menu = new[]
        {
            WarehouseNpcProtocol.ManagerActionSubId,
            ResolveWarehouseManagerStateSubId(
                snapshot.Capacity,
                _warehouseExpansionPolicy)
        };
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npc.InteractionId,
                route.DialogIndex,
                menu),
            cancellationToken,
            "WarehouseManagerMenu");
    }

    private async Task SendWarehouseExpansionResultAsync(
        uint npcId,
        WarehouseOperationIdentity identity,
        WarehouseExpansionExecutionResult execution,
        WarehouseExpansionExecutionReceipt receipt,
        CancellationToken cancellationToken)
    {
        var nativeResult = receipt.Status switch
        {
            WarehouseExpansionResultStatus.Expanded =>
                WarehouseCapacityPolicy.SuccessSubId(
                    receipt.CurrentCapacity),
            WarehouseExpansionResultStatus.InsufficientKeys =>
                WarehouseCapacityPolicy.InsufficientKeysSubId(
                    checked(receipt.PreviousCapacity +
                        WarehouseCapacityPolicy.SlotsPerBox),
                    receipt.RequiredKeyCount),
            WarehouseExpansionResultStatus.AlreadyMaximum =>
                WarehouseCapacityPolicy.StateSubId(
                    receipt.CurrentCapacity,
                    receipt.CurrentCapacity,
                    nextKeyCost: 0),
            _ => WarehouseNpcProtocol.ManagerGenericResultSubId
        };

        if (receipt.Succeeded)
        {
            if (ShouldEmitWarehouseExpansionDeleteAcknowledgement(
                    execution.Disposition))
            {
                foreach (var deletedSlot in receipt.KeyMutations
                             .Where(static mutation =>
                                 mutation.AfterLocation is null)
                             .Select(static mutation => mutation.BeforeSlot)
                             .Distinct()
                             .Order())
                {
                    await _session.SendAsync(
                        PacketBuilder.StorageItemKitBagDelete(deletedSlot),
                        cancellationToken,
                        "WarehouseExpansionKeyDeleteAck");
                }
            }
            await SendKitBagRefreshAsync(cancellationToken);
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                WarehouseNpcProtocol.ManagerDialogIndex,
                nativeResult),
            cancellationToken,
            "WarehouseExpansionResult");
        if (identity.IsSecureClient)
        {
            var secureDisposition = execution.Disposition switch
            {
                WarehouseExpansionExecutionDisposition.Committed
                    when receipt.Succeeded =>
                    SecureLegacyCommandDisposition.Applied,
                WarehouseExpansionExecutionDisposition.Duplicate =>
                    SecureLegacyCommandDisposition.Replayed,
                WarehouseExpansionExecutionDisposition.RequestHashConflict =>
                    SecureLegacyCommandDisposition.Conflict,
                _ => SecureLegacyCommandDisposition.Rejected
            };
            await SendSecureGearMentorResultAsync(
                identity.OperationId,
                CommandFamily.WarehouseExpansion,
                nativeResult,
                secureDisposition,
                receipt.WarehouseRevision,
                cancellationToken);
        }
    }

    private async Task SendWarehouseExpansionRejectedAsync(
        uint npcId,
        WarehouseExpansionExecutionDisposition disposition,
        Guid? clientOperationId,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                WarehouseNpcProtocol.ManagerDialogIndex,
                WarehouseNpcProtocol.ManagerGenericResultSubId),
            cancellationToken,
            "WarehouseExpansionRejected");
        if (_session.IsSecure && clientOperationId.HasValue)
        {
            await SendSecureGearMentorResultAsync(
                clientOperationId.Value,
                CommandFamily.WarehouseExpansion,
                WarehouseNpcProtocol.ManagerGenericResultSubId,
                disposition ==
                    WarehouseExpansionExecutionDisposition.RequestHashConflict
                    ? SecureLegacyCommandDisposition.Conflict
                    : SecureLegacyCommandDisposition.Rejected,
                0,
                cancellationToken);
        }
    }

    internal static bool
        ShouldEmitWarehouseExpansionDeleteAcknowledgement(
        WarehouseExpansionExecutionDisposition disposition) =>
        disposition == WarehouseExpansionExecutionDisposition.Committed;

    internal static int ResolveWarehouseManagerStateSubId(
        int capacity,
        WarehouseExpansionPolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (!WarehouseCapacityPolicy.IsValidCapacity(capacity))
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (capacity >= policy.MaximumCapacity)
        {
            return WarehouseCapacityPolicy.StateSubId(
                policy.MaximumCapacity,
                policy.MaximumCapacity,
                nextKeyCost: 0);
        }

        var nextLevel = policy.NextLevelForCapacity(capacity);
        return WarehouseCapacityPolicy.StateSubId(
            capacity,
            policy.MaximumCapacity,
            nextLevel.KeyCost);
    }

    internal static void ValidateWarehouseExpansionReceipt(
        int expectedCharacterId,
        int expectedRealmId,
        WarehouseExpansionExecutionReceipt receipt,
        WarehouseExpansionExecutionDisposition disposition,
        WarehouseExpansionPolicySnapshot currentPolicy)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        if (receipt.CharacterId != expectedCharacterId ||
            receipt.RealmId != expectedRealmId ||
            receipt.ActionSubId != WarehouseNpcProtocol.ManagerActionSubId ||
            disposition == WarehouseExpansionExecutionDisposition.Committed &&
                (receipt.PolicyRevision != currentPolicy.Revision ||
                 !string.Equals(
                     receipt.PolicySha256,
                     currentPolicy.Sha256,
                     StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "The warehouse expansion receipt identity is inconsistent.");
        }
    }
}
