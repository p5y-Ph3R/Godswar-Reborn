using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    /// <summary>
    /// Resolves a permanent command-inbox outcome before ephemeral NPC map,
    /// visibility, dialogue, or behavior state is allowed to reject a retry.
    /// A miss returns false so the caller can apply its normal route guard.
    /// Any unknown provider outcome stays pending and returns true.
    /// </summary>
    private async Task<bool>
        TryReplayDurableGearMentorBeforeRouteRejectionAsync(
            GamePacket packet,
            uint npcId,
            int wireSubId,
            CancellationToken cancellationToken)
    {
        if (!packet.ClientOperationId.HasValue ||
            !_session.IsSecure ||
            _account is null ||
            _character is null)
        {
            return false;
        }

        var family = ResolveSecureGearMentorCommandFamily(wireSubId);
        if (!family.HasValue)
        {
            return false;
        }

        ClearGearEnhancerSelection();
        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        try
        {
            switch (family.Value)
            {
                case CommandFamily.GearMentorMakeAttributeStone:
                    if (_makeAttributeStoneCommands is null)
                    {
                        RecordUnresolvedReplayProvider(family.Value);
                        return true;
                    }

                    var stone =
                        await _makeAttributeStoneCommands.TryReplayAsync(
                            subject,
                            packet.ClientOperationId.Value,
                            cancellationToken);
                    if (stone.Disposition ==
                        MakeAttributeStoneExecutionDisposition
                            .ReplayNotFound)
                    {
                        return false;
                    }
                    if (!stone.IsDurable)
                    {
                        RecordUnresolvedReplayOutcome(
                            family.Value,
                            stone.Disposition.ToString());
                        return true;
                    }

                    await CompleteUnroutedMakeStoneReplayAsync(
                        npcId,
                        packet.ClientOperationId.Value,
                        stone.Receipt!,
                        cancellationToken);
                    return true;

                case CommandFamily.GearMentorTransformCrystal:
                case CommandFamily.GearMentorCombineGemPieces:
                    if (_gearMentorMaterialConversionCommands is null)
                    {
                        RecordUnresolvedReplayProvider(family.Value);
                        return true;
                    }

                    var conversion = family.Value ==
                        CommandFamily.GearMentorTransformCrystal
                        ? await _gearMentorMaterialConversionCommands
                            .TryReplayTransformAsync(
                                subject,
                                packet.ClientOperationId.Value,
                                cancellationToken)
                        : await _gearMentorMaterialConversionCommands
                            .TryReplayCombineAsync(
                                subject,
                                packet.ClientOperationId.Value,
                                cancellationToken);
                    if (conversion.Disposition ==
                        GearMentorMaterialConversionExecutionDisposition
                            .ReplayNotFound)
                    {
                        return false;
                    }
                    if (!conversion.IsDurable)
                    {
                        RecordUnresolvedReplayOutcome(
                            family.Value,
                            conversion.Disposition.ToString());
                        return true;
                    }

                    await CompleteUnroutedMaterialConversionReplayAsync(
                        npcId,
                        packet.ClientOperationId.Value,
                        family.Value,
                        conversion.Receipt!,
                        cancellationToken);
                    return true;

                default:
                    return false;
            }
        }
        catch (OperationCanceledException)
        {
            CommandMetrics.Record(
                family.Value,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.Cancelled);
            throw;
        }
        catch (Exception ex)
        {
            // A lookup or projection failure cannot prove whether a prior
            // request committed. Never replace that uncertainty with a route
            // rejection that would settle the UUID incorrectly.
            CommandMetrics.Record(
                family.Value,
                CommandIdentityStrength.ClientOperationId,
                CommandOutcome.ProviderUnavailable);
            Console.Error.WriteLine(
                "[gear-mentor] pre-route durable replay unresolved " +
                $"account={_account.Id} character={_character.Name} " +
                $"family={family.Value}: {ex.Message}");
            return true;
        }
    }

    private async Task CompleteUnroutedMakeStoneReplayAsync(
        uint npcId,
        Guid clientOperationId,
        MakeAttributeStoneExecutionReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (receipt.CharacterId != _character!.Id)
        {
            throw new InvalidDataException(
                "The Make Attribute Stone replay receipt belongs to a " +
                "different character.");
        }

        var kitBagBeforeReplay = _character.KitBag;
        await ReloadDurableInventoryProjectionAsync(cancellationToken);
        await SendUnroutedReplayResponseAsync(
            npcId,
            clientOperationId,
            CommandFamily.GearMentorMakeAttributeStone,
            receipt.NativeResultSubId,
            receipt.InventoryRevision,
            receipt.Status == MakeAttributeStoneResultStatus.Succeeded,
            kitBagBeforeReplay,
            cancellationToken);
    }

    private async Task
        CompleteUnroutedMaterialConversionReplayAsync(
            uint npcId,
            Guid clientOperationId,
            CommandFamily family,
            GearMentorMaterialConversionExecutionReceipt receipt,
            CancellationToken cancellationToken)
    {
        if (receipt.Family != family ||
            receipt.CharacterId != _character!.Id)
        {
            throw new InvalidDataException(
                "The material-conversion replay receipt identity is " +
                "inconsistent.");
        }

        var kitBagBeforeReplay = _character.KitBag;
        await ReloadDurableInventoryProjectionAsync(cancellationToken);
        await SendUnroutedReplayResponseAsync(
            npcId,
            clientOperationId,
            family,
            receipt.NativeResultSubId,
            receipt.InventoryRevision,
            receipt.Status ==
                GearMentorMaterialConversionResultStatus.Succeeded,
            kitBagBeforeReplay,
            cancellationToken);
    }

    private async Task SendUnroutedReplayResponseAsync(
        uint npcId,
        Guid clientOperationId,
        CommandFamily family,
        int nativeResultSubId,
        long inventoryRevision,
        bool successfulMutation,
        string kitBagBeforeReplay,
        CancellationToken cancellationToken)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.Duplicate);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                GearEnhancerProtocol.DialogIndex,
                nativeResultSubId),
            cancellationToken,
            "NpcFunctionActionResponse");
        if (successfulMutation)
        {
            foreach (var acknowledgement in
                PacketBuilder.KitBagMutationDeletionAcknowledgements(
                    kitBagBeforeReplay,
                    _character!.KitBag))
            {
                await _session.SendAsync(
                    acknowledgement,
                    cancellationToken,
                    "GearMentorKitBagDeleteAck");
            }
        }

        await SendKitBagRefreshAsync(cancellationToken);
        await SendSecureGearMentorResultAsync(
            clientOperationId,
            family,
            nativeResultSubId,
            SecureLegacyCommandDisposition.Replayed,
            inventoryRevision,
            cancellationToken);
        Console.WriteLine(
            "[gear-mentor] replayed durable outcome before route " +
            $"rejection account={_account!.Id} " +
            $"character={_character!.Name} family={family} " +
            $"revision={inventoryRevision}");
    }

    private static void RecordUnresolvedReplayProvider(
        CommandFamily family)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.ProviderUnavailable);
    }

    private static void RecordUnresolvedReplayOutcome(
        CommandFamily family,
        string outcome)
    {
        CommandMetrics.Record(
            family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.PreconditionFailed);
        Console.WriteLine(
            "[gear-mentor] pre-route durable replay remains pending " +
            $"family={family} outcome={outcome}");
    }
}
