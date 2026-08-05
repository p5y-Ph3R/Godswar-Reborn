using Godswar.Server.Application.Inventory;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void PrepareHolyStoneSelectionContext(
        uint npcId,
        int dialogIndex,
        int subId)
    {
        ClearGearEnhancerSelection();
        if (_account is null || _character is null)
        {
            return;
        }

        if (subId == HolyStoneProtocol.CombineSubId)
        {
            _holyStoneCombinationSelectionContext =
                new HolyStoneCombinationSelectionContext(
                    _account.Id,
                    _character.Id,
                    npcId,
                    dialogIndex,
                    DateTimeOffset.UtcNow +
                        GearEnhancerProtocol.SelectionContextLifetime);
            return;
        }
        if (subId is not (
                HolyStoneProtocol.AdvancedDrillSubId or
                HolyStoneProtocol.UpgradeSubId or
                HolyStoneProtocol.ImplementSpiritSubId))
        {
            return;
        }

        // The stock Advanced Drill controls report their authoritative bag
        // page and cell through opcode 10193. The final action 701 packet then
        // contains only the page-local cells, so retain the bounded selection
        // context until that one confirmation is consumed.
        _gearEnhancerSelectionContext = new GearEnhancerSelectionContext(
            _account.Id,
            _character.Id,
            npcId,
            dialogIndex,
            operation: null,
            DateTimeOffset.UtcNow +
            GearEnhancerProtocol.SelectionContextLifetime);
    }

    private bool TryResolveCombinationSelections(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation != HolyStoneCommandOperation.Combine ||
            _account is null ||
            _character is null)
        {
            return true;
        }

        var context = _holyStoneCombinationSelectionContext;
        try
        {
            if (context is null ||
                context.NpcId != npcId ||
                context.DialogIndex != dialogIndex ||
                !context.IsActiveFor(
                    _account.Id,
                    _character.Id,
                    DateTimeOffset.UtcNow) ||
                !context.TryConsumePostResultCommit(
                    _character.KitBag,
                    out var selections) ||
                selections.Count !=
                    HolyStoneCombinationSelectionContext
                        .RequiredSelectionCount)
            {
                return false;
            }

            var selectedSlots = selections
                .Select(static selection => selection.KitBagSlot)
                .ToArray();
            if (!CombinationIntentMatchesSelections(
                    intent,
                    selectedSlots))
            {
                return false;
            }

            resolved = intent with
            {
                TargetLocation = HolyStoneTargetLocation.KitBag,
                TargetSlot = selectedSlots[0],
                StoneKitBagSlot = selectedSlots[1],
                CatalystKitBagSlot = selectedSlots[2],
                ThirdMaterialKitBagSlot = selectedSlots[3]
            };
            return true;
        }
        finally
        {
            // Four selected rows authorize one attempt only. A repeated raw
            // action or a new secure UUID must stage four fresh snapshots.
            ClearGearEnhancerSelection();
        }
    }

    private bool TryResolveRawCombinationSelections(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation != HolyStoneCommandOperation.Combine)
        {
            return true;
        }
        if (intent.TargetSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            intent.StoneKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            intent.CatalystKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            intent.ThirdMaterialKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot)
        {
            // Raw classification already consumed and immutable-validated the
            // one-shot four-slot snapshot before asynchronous NPC routing.
            return true;
        }

        return TryResolveCombinationSelections(
            npcId,
            dialogIndex,
            intent,
            out resolved);
    }

    private static bool CombinationIntentMatchesSelections(
        HolyStoneWireIntent intent,
        IReadOnlyList<int> selections)
    {
        if (selections.Count !=
            HolyStoneCombinationSelectionContext.RequiredSelectionCount)
        {
            return false;
        }

        // A staged boundary produced by the stock client can carry scratch
        // references; a parsed canonical command must match all four roles.
        return intent.TargetSlot < 0 ||
            intent.TargetSlot == selections[0] &&
            intent.StoneKitBagSlot == selections[1] &&
            intent.CatalystKitBagSlot == selections[2] &&
            intent.ThirdMaterialKitBagSlot == selections[3];
    }

    private bool TryResolveAdvancedDrillSelections(
        uint npcId,
        int dialogIndex,
        IReadOnlyList<int> args,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation !=
                HolyStoneCommandOperation.AdvancedDrill ||
            _account is null ||
            _character is null)
        {
            return true;
        }

        var context = _gearEnhancerSelectionContext;
        if (context is null ||
            context.NpcId != npcId ||
            context.DialogIndex != dialogIndex ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                DateTimeOffset.UtcNow))
        {
            // Programmatic secure clients may send canonical page-encoded
            // slots directly. Preserve that exact protocol path when no stock
            // UI selection context exists.
            return true;
        }

        try
        {
            if (args.Count != HolyStoneProtocol.FunctionArgumentCount ||
                !context.TryResolveNativeSlots(
                    GearEnhancerSelectionShape.MenuSelection,
                    minimumCount: 2,
                    maximumCount: 2,
                    out var selections))
            {
                return false;
            }

            var targetReference =
                args[HolyStoneProtocol.TargetArgumentIndex];
            var stoneReference =
                args[HolyStoneProtocol.StoneArgumentIndex];
            var targets = selections
                .Where(selection => ReferenceMatchesSelection(
                    targetReference,
                    selection.KitBagSlot))
                .Take(2)
                .ToArray();
            var stones = selections
                .Where(selection => ReferenceMatchesSelection(
                    stoneReference,
                    selection.KitBagSlot))
                .Take(2)
                .ToArray();
            if (targets.Length != 1 ||
                stones.Length != 1 ||
                targets[0].KitBagSlot == stones[0].KitBagSlot ||
                !SelectionStillMatches(targets[0]) ||
                !SelectionStillMatches(stones[0]))
            {
                return false;
            }

            resolved = intent with
            {
                TargetLocation = HolyStoneTargetLocation.KitBag,
                TargetSlot = targets[0].KitBagSlot,
                StoneKitBagSlot = stones[0].KitBagSlot
            };
            return true;
        }
        finally
        {
            // Native confirmation is one-shot. A repeated raw packet cannot
            // reuse an earlier pair of UI selections.
            ClearGearEnhancerSelection();
        }
    }

    private bool TryResolveUpgradeSelections(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation != HolyStoneCommandOperation.Upgrade ||
            _account is null ||
            _character is null)
        {
            return true;
        }

        var context = _gearEnhancerSelectionContext;
        if (context is null ||
            context.NpcId != npcId ||
            context.DialogIndex != dialogIndex ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                DateTimeOffset.UtcNow))
        {
            // A programmatic secure client may send the canonical three bag
            // references inline. The stock UI path always has a staged
            // context because its controls report selections through 10193.
            return intent.TargetSlot >=
                       HolyStoneCommandEnvelope.MinimumKitBagSlot &&
                   intent.StoneKitBagSlot >=
                       HolyStoneCommandEnvelope.MinimumKitBagSlot;
        }

        try
        {
            if (!context.TryResolveNativeSlots(
                    GearEnhancerSelectionShape.MenuSelection,
                    minimumCount: 2,
                    maximumCount: 3,
                    out var selections) ||
                selections.Any(selection =>
                    !SelectionStillMatches(selection)) ||
                selections.Select(selection => selection.KitBagSlot)
                    .Distinct()
                    .Count() != selections.Count)
            {
                return false;
            }

            // NpcFunEment exposes these controls in a fixed order: Holy
            // Stone, Eclipse Stone, then optional Goddess/Evasion catalyst.
            resolved = intent with
            {
                TargetLocation = HolyStoneTargetLocation.KitBag,
                TargetSlot = selections[0].KitBagSlot,
                StoneKitBagSlot = selections[1].KitBagSlot,
                CatalystKitBagSlot = selections.Count == 3
                    ? selections[2].KitBagSlot
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot
            };
            return true;
        }
        finally
        {
            // The stock confirmation is one-shot. Never let a second action
            // reuse selections from an earlier probabilistic upgrade.
            ClearGearEnhancerSelection();
        }
    }

    private bool TryResolveRawUpgradeSelections(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation != HolyStoneCommandOperation.Upgrade)
        {
            return true;
        }
        if (intent.TargetLocation == HolyStoneTargetLocation.KitBag &&
            intent.TargetSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            intent.StoneKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot)
        {
            // Raw routing already consumed the exact one-second clear
            // snapshot before awaiting NPC content. Preserve only that
            // pre-resolved intent; no arbitrary action-401 scratch field can
            // construct it.
            return true;
        }

        try
        {
            if (!TryReadRawUpgradeSelectionCommit(
                    npcId,
                    dialogIndex,
                    out var selections))
            {
                return false;
            }

            resolved = intent with
            {
                TargetLocation = HolyStoneTargetLocation.KitBag,
                TargetSlot = selections[0].KitBagSlot,
                StoneKitBagSlot = selections[1].KitBagSlot,
                CatalystKitBagSlot = selections.Count == 3
                    ? selections[2].KitBagSlot
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot
            };
            return true;
        }
        finally
        {
            // Raw action 401 has no client operation UUID. Its exact ordered
            // selection snapshot is therefore the one-shot authorization
            // token, and every final attempt discards the context.
            ClearGearEnhancerSelection();
        }
    }

    private bool TryResolveImplementSpiritSelections(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation !=
                HolyStoneCommandOperation.ImplementSpirit ||
            _account is null ||
            _character is null)
        {
            return true;
        }

        var context = _gearEnhancerSelectionContext;
        if (context is null ||
            context.NpcId != npcId ||
            context.DialogIndex != dialogIndex ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                DateTimeOffset.UtcNow))
        {
            return intent.TargetSlot >=
                       HolyStoneCommandEnvelope.MinimumKitBagSlot &&
                   intent.StoneKitBagSlot >=
                       HolyStoneCommandEnvelope.MinimumKitBagSlot;
        }

        try
        {
            if (!context.TryResolveNativeSlots(
                    GearEnhancerSelectionShape.MenuSelection,
                    minimumCount: 2,
                    maximumCount: 3,
                    out var selections) ||
                selections.Any(selection =>
                    !SelectionStillMatches(selection)) ||
                selections.Select(selection => selection.KitBagSlot)
                    .Distinct().Count() != selections.Count)
            {
                return false;
            }

            resolved = intent with
            {
                TargetLocation = HolyStoneTargetLocation.KitBag,
                TargetSlot = selections[0].KitBagSlot,
                StoneKitBagSlot = selections[1].KitBagSlot,
                CatalystKitBagSlot = selections.Count == 3
                    ? selections[2].KitBagSlot
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot
            };
            return true;
        }
        finally
        {
            ClearGearEnhancerSelection();
        }
    }

    private bool TryResolveRawImplementSpiritSelections(
        uint npcId,
        int dialogIndex,
        HolyStoneWireIntent intent,
        out HolyStoneWireIntent resolved)
    {
        resolved = intent;
        if (intent.Operation !=
                HolyStoneCommandOperation.ImplementSpirit)
        {
            return true;
        }
        if (intent.TargetSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot &&
            intent.StoneKitBagSlot >=
                HolyStoneCommandEnvelope.MinimumKitBagSlot)
        {
            return true;
        }

        try
        {
            if (!TryReadRawUpgradeSelectionCommit(
                    npcId,
                    dialogIndex,
                    out var selections))
            {
                return false;
            }

            resolved = intent with
            {
                TargetLocation = HolyStoneTargetLocation.KitBag,
                TargetSlot = selections[0].KitBagSlot,
                StoneKitBagSlot = selections[1].KitBagSlot,
                CatalystKitBagSlot = selections.Count == 3
                    ? selections[2].KitBagSlot
                    : HolyStoneCommandEnvelope.NoStoneKitBagSlot
            };
            return true;
        }
        finally
        {
            ClearGearEnhancerSelection();
        }
    }

    private bool TryReadRawUpgradeSelectionCommit(
        uint npcId,
        int dialogIndex,
        out IReadOnlyList<GearEnhancerSelectionSnapshot> selections)
    {
        selections = [];
        if (_account is null || _character is null)
        {
            return false;
        }

        var context = _gearEnhancerSelectionContext;
        if (context is null ||
            context.NpcId != npcId ||
            context.DialogIndex != dialogIndex ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                DateTimeOffset.UtcNow) ||
            !(context.TryResolveClearedNativeSlots(
                  minimumCount: 2,
                  maximumCount: 3,
                  out selections) ||
              context.TryResolvePostResultRawUpgradeCommit(
                  minimumCount: 2,
                  maximumCount: 3,
                  out selections)) ||
            selections.Select(selection => selection.KitBagSlot)
                .Distinct()
                .Count() != selections.Count ||
            selections.Any(selection =>
                !SelectionStillMatches(selection)))
        {
            selections = [];
            return false;
        }

        return true;
    }

    private bool SelectionStillMatches(
        GearEnhancerSelectionSnapshot selection) =>
        KitBagSlots.GetItem(
                _character!.KitBag,
                selection.KitBagSlot)
            .Equals(selection.ExpectedItem);

    private static bool ReferenceMatchesSelection(
        int reference,
        int kitBagSlot) =>
        reference == HolyStoneProtocol.EncodeKitBagReference(kitBagSlot) ||
        reference == kitBagSlot %
            GearEnhancerItemSelectionPacket.SlotsPerPage;
}
