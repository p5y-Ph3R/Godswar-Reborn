using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Characters;
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
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return true;
        }

        ClearGearEnhancerSelection();
        var subject = new CommandSubject(
            _account.Id,
            _character.Id);
        try
        {
            switch (family.Value)
            {
                case CommandFamily.GearMentorEnhanceAttribute:
                case CommandFamily.GearMentorAddAttribute:
                case CommandFamily.GearMentorDeleteAttribute:
                    if (_gearEnhancementCommands is null)
                    {
                        RecordUnresolvedReplayProvider(family.Value);
                        return true;
                    }

                    var enhancement =
                        await _gearEnhancementCommands.TryReplayAsync(
                            subject,
                            ownership,
                            GearEnhancementOperationFromFamily(
                                family.Value),
                            packet.ClientOperationId.Value,
                            cancellationToken);
                    if (!RevalidateCurrentPlayerOwnership(ownership))
                    {
                        return true;
                    }

                    if (enhancement.Disposition ==
                        GearEnhancementExecutionDisposition.ReplayNotFound)
                    {
                        return false;
                    }
                    if (!enhancement.IsDurable)
                    {
                        RecordUnresolvedReplayOutcome(
                            family.Value,
                            enhancement.Disposition.ToString());
                        return true;
                    }

                    await CompleteUnroutedGearEnhancementReplayAsync(
                        packet.ClientOperationId.Value,
                        enhancement.Receipt!,
                        ownership,
                        cancellationToken);
                    return true;

                case CommandFamily.GearMentorDecomposeGear:
                    if (_gearMentorDecomposeGearCommands is null)
                    {
                        RecordUnresolvedReplayProvider(family.Value);
                        return true;
                    }

                    var decompose =
                        await _gearMentorDecomposeGearCommands
                            .TryReplayAsync(
                                subject,
                                ownership,
                                packet.ClientOperationId.Value,
                                cancellationToken);
                    if (!RevalidateCurrentPlayerOwnership(ownership))
                    {
                        return true;
                    }

                    if (decompose.Disposition ==
                        GearMentorDecomposeGearExecutionDisposition
                            .ReplayNotFound)
                    {
                        return false;
                    }
                    if (!decompose.IsDurable)
                    {
                        RecordUnresolvedReplayOutcome(
                            family.Value,
                            decompose.Disposition.ToString());
                        return true;
                    }

                    await CompleteUnroutedDecomposeReplayAsync(
                        npcId,
                        packet.ClientOperationId.Value,
                        decompose.Receipt!,
                        ownership,
                        cancellationToken);
                    return true;

                case CommandFamily.GearMentorMakeAttributeStone:
                    if (_makeAttributeStoneCommands is null)
                    {
                        RecordUnresolvedReplayProvider(family.Value);
                        return true;
                    }

                    var stone =
                        await _makeAttributeStoneCommands.TryReplayAsync(
                            subject,
                            ownership,
                            packet.ClientOperationId.Value,
                            cancellationToken);
                    if (!RevalidateCurrentPlayerOwnership(ownership))
                    {
                        return true;
                    }

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
                        ownership,
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
                                ownership,
                                packet.ClientOperationId.Value,
                                cancellationToken)
                        : await _gearMentorMaterialConversionCommands
                            .TryReplayCombineAsync(
                                subject,
                                ownership,
                                packet.ClientOperationId.Value,
                                cancellationToken);
                    if (!RevalidateCurrentPlayerOwnership(ownership))
                    {
                        return true;
                    }

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
                        ownership,
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
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return true;
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

    private async Task CompleteUnroutedGearEnhancementReplayAsync(
        Guid clientOperationId,
        GearEnhancementExecutionReceipt receipt,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (receipt.CharacterId != _character!.Id ||
            receipt.Family !=
                GearEnhancementCommandEnvelope.Family(
                    receipt.Operation))
        {
            throw new InvalidDataException(
                "The Gear Enhancement replay receipt identity is " +
                "inconsistent.");
        }

        CommandMetrics.Record(
            receipt.Family,
            CommandIdentityStrength.ClientOperationId,
            CommandOutcome.Duplicate);
        var kitBagBeforeReplay = _character.KitBag;
        await ReloadDurableInventoryProjectionAsync(
            ownership,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await SendDurableGearEnhancementReceiptAsync(
            clientOperationId,
            receipt,
            GearEnhancementExecutionDisposition.Duplicate,
            kitBagBeforeReplay,
            cancellationToken);
        Console.WriteLine(
            "[gear-enhancement] replayed durable outcome before route " +
            $"rejection account={_account!.Id} " +
            $"character={_character.Name} family={receipt.Family} " +
            $"revision={receipt.InventoryRevision}");
    }

    private async Task CompleteUnroutedMakeStoneReplayAsync(
        uint npcId,
        Guid clientOperationId,
        MakeAttributeStoneExecutionReceipt receipt,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (receipt.CharacterId != _character!.Id)
        {
            throw new InvalidDataException(
                "The Make Attribute Stone replay receipt belongs to a " +
                "different character.");
        }

        var kitBagBeforeReplay = _character.KitBag;
        await ReloadDurableInventoryProjectionAsync(
            ownership,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

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

    private async Task CompleteUnroutedDecomposeReplayAsync(
        uint npcId,
        Guid clientOperationId,
        GearMentorDecomposeGearExecutionReceipt receipt,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        if (receipt.Family != CommandFamily.GearMentorDecomposeGear ||
            receipt.CharacterId != _character!.Id)
        {
            throw new InvalidDataException(
                "The Decompose replay receipt identity is inconsistent.");
        }

        var kitBagBeforeReplay = _character.KitBag;
        await ReloadDurableInventoryProjectionAsync(
            ownership,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await SendUnroutedReplayResponseAsync(
            npcId,
            clientOperationId,
            CommandFamily.GearMentorDecomposeGear,
            receipt.NativeResultSubId,
            receipt.InventoryRevision,
            receipt.Status ==
                GearMentorDecomposeGearResultStatus.Succeeded,
            kitBagBeforeReplay,
            cancellationToken);
    }

    private async Task
        CompleteUnroutedMaterialConversionReplayAsync(
            uint npcId,
            Guid clientOperationId,
            CommandFamily family,
            GearMentorMaterialConversionExecutionReceipt receipt,
            PlayerOwnershipFence ownership,
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
        await ReloadDurableInventoryProjectionAsync(
            ownership,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

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

    private static GearEnhancementCommandOperation
        GearEnhancementOperationFromFamily(CommandFamily family) =>
        family switch
        {
            CommandFamily.GearMentorEnhanceAttribute =>
                GearEnhancementCommandOperation.Enhance,
            CommandFamily.GearMentorAddAttribute =>
                GearEnhancementCommandOperation.Add,
            CommandFamily.GearMentorDeleteAttribute =>
                GearEnhancementCommandOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };
}
