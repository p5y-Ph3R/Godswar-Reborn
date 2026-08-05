using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HolyStoneCombinationSelectionChecks
{
    public const string CheckName =
        "Holy Stone four-slot combination selection boundary";

    public static Task RunAsync()
    {
        CheckInitialClearThenAction();
        CheckResultActionThenClear();
        CheckStaleAndWrongOrderFailClosed();
        return Task.CompletedTask;
    }

    private static void CheckInitialClearThenAction()
    {
        var now = DateTimeOffset.UtcNow;
        var bag = CreateBag();
        var context = CreateContext(now);
        StageFour(context, bag);

        for (var slot = 0; slot < 4; slot++)
        {
            context.Apply(Selection(slot, selected: false), bag);
        }

        Check.True(
            context.TryConsumePostResultCommit(
                bag,
                out var selections),
            "initial Combination accepts only its completed four-control " +
            "clear burst");
        Check.True(
            selections.Select(static value => value.KitBagSlot)
                .SequenceEqual([0, 1, 2, 3]),
            "major and three fodder roles retain native ItemBtn order");
        Check.True(
            !context.TryConsumePostResultCommit(bag, out _),
            "the initial four-slot authorization is one-shot");
    }

    private static void CheckResultActionThenClear()
    {
        var now = DateTimeOffset.UtcNow;
        var bag = CreateBag();
        var context = CreateContext(now);
        context.AllowPostResultCommit();
        StageFour(context, bag);

        Check.True(
            context.TryConsumePostResultCommit(
                bag,
                out var selections) &&
            selections.Count == 4,
            "result panel accepts its exact action-before-clear ordering");
        Check.True(
            !context.TryConsumePostResultCommit(bag, out _),
            "result-panel live authorization is consumed once");
    }

    private static void CheckStaleAndWrongOrderFailClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var bag = CreateBag();
        var wrongOrder = CreateContext(now);
        StageFour(wrongOrder, bag);
        wrongOrder.Apply(Selection(1, selected: false), bag);
        wrongOrder.Apply(Selection(0, selected: false), bag);
        wrongOrder.Apply(Selection(2, selected: false), bag);
        wrongOrder.Apply(Selection(3, selected: false), bag);
        Check.True(
            !wrongOrder.TryConsumePostResultCommit(bag, out _),
            "out-of-order native clears cannot authorize Combination");

        var stale = CreateContext(now);
        stale.AllowPostResultCommit();
        StageFour(stale, bag);
        var changed = KitBagSlots.SetSlot(
            bag,
            3,
            (KitBagSlots.GetItem(bag, 3) with { Grade = 5 })
                .ToCompactString());
        Check.True(
            !stale.TryConsumePostResultCommit(changed, out _),
            "changed selected items fail immutable revalidation");
    }

    private static HolyStoneCombinationSelectionContext CreateContext(
        DateTimeOffset now) =>
        new(
            accountId: 7,
            characterId: 13,
            npcId: HolyStoneProtocol.SpartaNpcId,
            dialogIndex: HolyStoneProtocol.DialogIndex,
            expiresAt: now.AddMinutes(1),
            utcNow: () => now);

    private static string CreateBag()
    {
        var bag = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < 4; slot++)
        {
            var stone = CompactItemEntry.Empty with
            {
                Id = slot % 2 == 0
                    ? 9030u
                    : 9031u,
                Quality = 1,
                Grade = 4,
                Bound = 1,
                Stack = 1
            };
            bag = KitBagSlots.SetSlot(
                bag,
                slot,
                stone.ToCompactString());
        }
        return bag;
    }

    private static void StageFour(
        HolyStoneCombinationSelectionContext context,
        string bag)
    {
        for (var slot = 0; slot < 4; slot++)
        {
            var result = context.Apply(
                Selection(slot, selected: true),
                bag);
            Check.Equal(
                (int)HolyStoneCombinationSelectionStatus.Staged,
                (int)result.Status,
                $"Combination stages ordered slot {slot}");
        }
    }

    private static GearEnhancerItemSelectionPacket Selection(
        int slot,
        bool selected) =>
        new(
            BagPage: slot /
                GearEnhancerItemSelectionPacket.SlotsPerPage,
            PageSlot: slot %
                GearEnhancerItemSelectionPacket.SlotsPerPage,
            Selected: selected);
}
