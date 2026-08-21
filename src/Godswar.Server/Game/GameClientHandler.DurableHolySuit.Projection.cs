using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal readonly record struct HolySuitProjectionRevisions(
    long ProgressionRevision,
    long InventoryRevision);

internal sealed partial class GameClientHandler
{
    private async Task<HolySuitProjectionRevisions>
        ReloadDurableHolySuitProjectionAsync(
        PlayerOwnershipFence ownership,
        HolySuitExecutionReceipt receipt,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The Holy Suit owner changed during projection reload.");
        }

        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            accountSnapshot);
        var persistedSnapshot = accountSnapshot.Character;
        if (hydrated is null ||
            persistedSnapshot is null ||
            hydrated.Character.Id != _character!.Id)
        {
            throw new InvalidDataException(
                "The durable Holy Suit character could not be reloaded.");
        }

        ValidateHolySuitProjection(
            hydrated.Character,
            persistedSnapshot.Progression.Revision,
            persistedSnapshot.Loadout.InventoryRevision,
            receipt);
        ApplyDurableHolySuitProjection(
            _character,
            hydrated.Character);
        if (receipt.Committed)
        {
            _registry.UpdateCharacter(
                _session,
                _character,
                advanceWorldRevision: false);
        }
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        return new HolySuitProjectionRevisions(
            persistedSnapshot.Progression.Revision,
            persistedSnapshot.Loadout.InventoryRevision);
    }

    internal static void ApplyDurableHolySuitProjection(
        GameCharacter liveCharacter,
        GameCharacter persistedCharacter)
    {
        ArgumentNullException.ThrowIfNull(liveCharacter);
        ArgumentNullException.ThrowIfNull(persistedCharacter);
        if (liveCharacter.Id != persistedCharacter.Id ||
            liveCharacter.AccountId != persistedCharacter.AccountId)
        {
            throw new InvalidDataException(
                "A Holy Suit projection cannot change character identity.");
        }

        // Holy Suit commands only mutate fighter EXP and kit-bag items. They
        // never change equipped items, so importing calculated equipment
        // stats here would incorrectly clamp live HP/MP on a rejected request.
        liveCharacter.KitBag = persistedCharacter.KitBag;
        liveCharacter.Experience = persistedCharacter.Experience;
        liveCharacter.HolySuitPoints = persistedCharacter.HolySuitPoints;
    }

    internal static void ValidateHolySuitProjection(
        GameCharacter persistedCharacter,
        long progressionRevision,
        long inventoryRevision,
        HolySuitExecutionReceipt receipt)
    {
        if (progressionRevision < receipt.ProgressionRevision ||
            inventoryRevision < receipt.InventoryRevision)
        {
            throw new InvalidDataException(
                "The durable Holy Suit projection predates its receipt.");
        }
        if (progressionRevision == receipt.ProgressionRevision &&
            persistedCharacter.Experience !=
            receipt.CharacterExperienceAfter)
        {
            throw new InvalidDataException(
                "The durable Holy Suit EXP projection is stale.");
        }
        if (!receipt.Committed ||
            inventoryRevision > receipt.InventoryRevision)
        {
            return;
        }

        foreach (var mutation in receipt.Mutations)
        {
            var projected = KitBagSlots.GetItem(
                persistedCharacter.KitBag,
                mutation.KitBagSlot);
            if (!string.Equals(
                    projected.ToCompactString(),
                    mutation.AfterCompactItemState,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The durable Holy Suit item projection is stale.");
            }
        }
    }

    private void ValidateHolySuitReceipt(
        uint npcId,
        int dialogIndex,
        HolySuitCommandOperation operation,
        HolySuitExecutionReceipt receipt)
    {
        if (receipt.CharacterId != _character!.Id ||
            receipt.Operation != operation ||
            !HolySuitCommandEnvelope.AreEquivalentEndpoints(
                receipt.NpcId,
                receipt.DialogIndex,
                checked((int)npcId),
                dialogIndex) ||
            receipt.Family !=
                HolySuitCommandEnvelope.Family(operation) ||
            receipt.NativeResultSubId !=
                HolySuitNativeResults.GetResultSubId(
                    operation,
                    receipt.Status))
        {
            throw new InvalidDataException(
                "The Holy Suit receipt identity is inconsistent.");
        }
    }

    private async Task SendHolySuitAuthoritativeProjectionAsync(
        bool committed,
        CancellationToken cancellationToken)
    {
        if (!committed)
        {
            // Refresh the authoritative bag so a client-side ghost or edited
            // item is corrected, but a rejected economy command must not
            // produce an unrelated player-status/vitals refresh.
            await SendKitBagRefreshAsync(cancellationToken);
            return;
        }

        await _session.SendAsync(
            PacketBuilder.ExperienceGain(
                gainedExperience: 0,
                currentExperience: _character!.Experience),
            cancellationToken,
            "HolySuitExperienceRefresh");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "HolySuitPlayerStatus");
        await SendKitBagRefreshAsync(cancellationToken);
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character!)),
            cancellationToken,
            "HolySuitEquipmentEffectVisibility");
    }
}
