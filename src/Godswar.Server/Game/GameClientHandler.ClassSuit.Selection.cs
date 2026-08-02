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
        if (!hasInlineMutation &&
            !ClassSuitProtocol.IsExactNavigation(packet, subId))
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
            !context.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                expectedCount,
                expectedCount,
                out var selections))
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
                ClassSuitWireOperation.DeleteClassAttribute or
                ClassSuitWireOperation.UpgradeTierTwo or
                ClassSuitWireOperation.UpgradeTierThree or
                ClassSuitWireOperation.UpgradeTierFour => 2,
            ClassSuitWireOperation.AddClassAttribute => 3,
            _ => 0
        };
        return count != 0;
    }
}
