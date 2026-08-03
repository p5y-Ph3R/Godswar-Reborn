using Godswar.Server.Domain.World.Content;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool TryResolveClassSuitStagedMutation(
        GamePacket packet,
        NpcDialogueRouteDefinition route,
        uint npcId,
        int subId,
        IReadOnlyList<int> arguments,
        out ClassSuitWireIntent intent)
    {
        intent = default;
        if (_account is null ||
            _character is null ||
            !ClassSuitProtocol.TryResolveOperation(
                subId,
                out var operation) ||
            !TryGetClassSuitSelectionCount(
                operation,
                out var expectedCount))
        {
            return false;
        }

        var hasInlineMutation =
            ClassSuitProtocol.TryReadMutation(
                packet,
                out var inlineNpcId,
                out var inlineIntent) &&
            inlineNpcId == npcId &&
            inlineIntent.Operation == operation;
        var exactNavigation =
            ClassSuitProtocol.IsExactNavigation(packet, subId);
        var requiresCompletedClear =
            !hasInlineMutation && !exactNavigation;
        var selectionShape = GearEnhancerSelectionShape.MenuSelection;
        if (requiresCompletedClear)
        {
            selectionShape = GearEnhancerProtocol.ReadSelection(
                arguments,
                out _,
                out _,
                out _);
            if (selectionShape == GearEnhancerSelectionShape.Commit)
            {
                // Physical NpcFunBreak sends authoritative item choices in
                // opcode 10193. Values left in its final 10069 controls are
                // scratch data and must never replace the staged snapshot.
                selectionShape = GearEnhancerSelectionShape.MalformedCommit;
            }
        }
        if (!hasInlineMutation &&
            !exactNavigation &&
            selectionShape is not (
                GearEnhancerSelectionShape.MenuSelection or
                GearEnhancerSelectionShape.MalformedCommit))
        {
            return false;
        }

        var context = _gearEnhancerSelectionContext;
        var now = DateTimeOffset.UtcNow;
        if (context is null ||
            !context.IsActiveForSelection(
                _account.Id,
                _character.Id,
                now) ||
            context.NpcId != npcId ||
            context.DialogIndex != route.DialogIndex ||
            !(requiresCompletedClear
                ? context.TryResolveClearedNativeSlots(
                    expectedCount,
                    expectedCount,
                    out var selections)
                : context.TryResolveNativeSlots(
                    GearEnhancerSelectionShape.MenuSelection,
                    expectedCount,
                    expectedCount,
                    out selections)))
        {
            return false;
        }

        var slots = selections
            .Select(static selection => selection.KitBagSlot)
            .ToArray();
        if (slots.Distinct().Count() != slots.Length)
        {
            return false;
        }

        foreach (var selection in selections)
        {
            var current = KitBagSlots.GetItem(
                _character.KitBag,
                selection.KitBagSlot);
            if (current.IsEmpty || current != selection.ExpectedItem)
            {
                return false;
            }
        }

        var stagedIntent = new ClassSuitWireIntent(
            operation,
            slots[0],
            expectedCount >= 2
                ? slots[1]
                : ClassSuitProtocol.NoKitBagSlot,
            expectedCount >= 3
                ? slots[2]
                : ClassSuitProtocol.NoKitBagSlot);
        if (hasInlineMutation && stagedIntent != inlineIntent)
        {
            // A secure operation UUID is derived from the inline selection.
            // Never let unrelated staged UI state replace that exact intent.
            return false;
        }

        intent = stagedIntent;
        return true;
    }

    private static bool TryGetClassSuitSelectionCount(
        ClassSuitWireOperation operation,
        out int count)
    {
        count = operation switch
        {
            ClassSuitWireOperation.ConvertToCommon => 1,
            ClassSuitWireOperation.ExchangeTierOne or
                ClassSuitWireOperation.UpgradeTierTwo or
                ClassSuitWireOperation.UpgradeTierThree or
                ClassSuitWireOperation.UpgradeTierFour => 2,
            ClassSuitWireOperation.AddClassAttribute or
                ClassSuitWireOperation.DeleteClassAttribute => 3,
            _ => 0
        };
        return count != 0;
    }
}
