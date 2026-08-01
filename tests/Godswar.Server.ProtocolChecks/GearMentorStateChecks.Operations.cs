using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorStateChecks
{
    private static void CheckDecomposition()
    {
        Check.Equal(248, ClassSuitItemCatalog.ShippedItemCount, "generated Class Suit count");
        Check.Equal(ClassSuitItemCatalog.ShippedItemCount, ClassSuitItemCatalog.AllItemIds.Count, "Class Suit catalog list count");
        Check.Equal(
            "3A8202C4021C0486DEB30F0D7A9BECA72F632E01227DDF2F1B0F6BFC9434B47D",
            ClassSuitItemCatalog.CanonicalItemIdSha256,
            "Class Suit canonical item-ID hash");
        Check.True(
            ClassSuitItemCatalog.AllItemIds.SequenceEqual(ClassSuitItemCatalog.AllItemIds.OrderBy(static itemId => itemId)),
            "Class Suit IDs remain deterministically sorted");
        Check.True(
            ClassSuitItemCatalog.AllItemIds.All(ClassSuitItemCatalog.IsClassSuit),
            "every generated Class Suit ID resolves");
        Check.True(
            !ClassSuitItemCatalog.IsClassSuit(1004) && !ClassSuitItemCatalog.IsClassSuit(1030),
            "ordinary and elite gear are not misclassified as Class Suits");

        var (eligibleBag, eligibleRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, eligibleBag, 29, eligibleRequest, static _ => 0),
            eligibleBag,
            GearMentorStatus.PlayerLevelTooLow,
            "characters below Level 30 cannot decompose");

        var (invalidBag, invalidRequest) = StageDecomposition(
            (4, Material(9900, stack: 1)));
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, invalidBag, 30, invalidRequest, static _ => 0),
            invalidBag,
            GearMentorStatus.InvalidEquipment,
            "materials cannot be decomposed as gear");

        var (stackedBag, stackedRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 1, stack: 2, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, stackedBag, 30, stackedRequest, static _ => 0),
            stackedBag,
            GearMentorStatus.InvalidEquipment,
            "stacked equipment records cannot be decomposed");

        var (lowGearBag, lowGearRequest) = StageDecomposition(
            (4, Gear(1003, quality: 2, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, lowGearBag, 30, lowGearRequest, static _ => 0),
            lowGearBag,
            GearMentorStatus.EquipmentLevelTooLow,
            "Level 40 gear cannot be decomposed");

        var (plainBag, plainRequest) = StageDecomposition(
            (4, Gear(1004, quality: 1, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, plainBag, 30, plainRequest, static _ => 0),
            plainBag,
            GearMentorStatus.InsufficientEquipmentQuality,
            "common Grade 1 gear cannot be decomposed");

        var (classSuitBag, classSuitRequest) = StageDecomposition(
            (4, Gear(1032, quality: 2, grade: 1, attribute1: 0)));
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, classSuitBag, 30, classSuitRequest, static _ => 0),
            classSuitBag,
            GearMentorStatus.ClassSuit,
            "Class Suit I cannot be decomposed");

        foreach (var qualifyingGear in new[]
                 {
                     Gear(1004, quality: 2, grade: 1, attribute1: 0),
                     Gear(1004, quality: 1, grade: 2, attribute1: 0)
                 })
        {
            var (kitBag, request) = StageDecomposition((4, qualifyingGear));
            var result = GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, kitBag, 30, request, static _ => 0);
            Check.True(result.Committed, "Enhanced quality or Grade 2 independently qualifies");
        }

        var expectedGears = new[]
        {
            Gear(1004, quality: 2, grade: 1, bound: 0, attribute1: 0),
            Gear(1005, quality: 2, grade: 1, bound: 1, attribute1: 20),
            Gear(1006, quality: 2, grade: 1, bound: 0, attribute1: 40)
        };
        var expectedDustIds = new uint[] { 9900, 9902, 9910 };
        for (var count = 1; count <= 3; count++)
        {
            var staged = Enumerable.Range(0, count)
                .Select(index => (Slot: 10 + index, Item: expectedGears[index]))
                .ToArray();
            var (kitBag, request) = StageDecomposition(staged);
            var result = GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog,
                kitBag,
                30,
                request,
                candidateCount => candidateCount - 1);

            Check.True(result.Committed, $"decomposition accepts exactly {count} selected gear item(s)");
            Check.Equal(count, result.Outputs.Count, $"{count}-gear decomposition output record count");
            for (var index = 0; index < count; index++)
            {
                Check.Equal(expectedDustIds[index], result.Outputs[index].ItemId, $"gear {index} uses its matched Dust family");
                Check.Equal(expectedGears[index].Bound, result.Outputs[index].Bound, $"gear {index} Dust preserves binding");
                Check.True(
                    !Enumerable.Range(0, SlotCount)
                        .Select(slot => KitBagSlots.GetItem(result.UpdatedKitBag, slot))
                        .Any(item => item.Id == expectedGears[index].Id),
                    $"decomposed gear {expectedGears[index].Id} is consumed");
            }
        }

        var fourSelections = new[]
        {
            (Slot: 0, Item: Gear(1004, attribute1: 0)),
            (Slot: 1, Item: Gear(1005, attribute1: 0)),
            (Slot: 2, Item: Gear(1006, attribute1: 0)),
            (Slot: 3, Item: Gear(1007, attribute1: 0))
        };
        var (fourBag, fourRequest) = StageDecomposition(fourSelections);
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, fourBag, 30, fourRequest, static _ => 0),
            fourBag,
            GearMentorStatus.SelectionMissing,
            "decomposition rejects more than three selections");

        var noSelection = new GearMentorRequest(GearMentorOperation.Decompose, []);
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, GameDefaults.EmptyKitBag, 30, noSelection, static _ => 0),
            GameDefaults.EmptyKitBag,
            GearMentorStatus.SelectionMissing,
            "decomposition rejects an empty selection");

        var duplicateBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            4,
            Gear(1004, attribute1: 0).ToCompactString());
        var duplicateSelection = GearMentorSlotSelection.Capture(duplicateBag, 4);
        var duplicateRequest = new GearMentorRequest(
            GearMentorOperation.Decompose,
            [duplicateSelection, duplicateSelection]);
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, duplicateBag, 30, duplicateRequest, static _ => 0),
            duplicateBag,
            GearMentorStatus.DuplicateKitBagSlot,
            "decomposition rejects duplicate bag slots");

        var (matchedBag, matchedRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 2, attribute1: 0, attribute2: 50)));
        var matchedResult = GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog,
            matchedBag,
            30,
            matchedRequest,
            candidateCount =>
            {
                Check.Equal(2, candidateCount, "decomposition limits random candidates to appended attributes");
                return 1;
            });
        Check.Equal(9911u, matchedResult.Outputs.Single().ItemId, "second matched attribute yields Psychic Dust");

        var (fallbackBag, fallbackRequest) = StageDecomposition(
            (4, Gear(1004, quality: 2, grade: 1)));
        var fallbackResult = GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog,
            fallbackBag,
            30,
            fallbackRequest,
            candidateCount =>
            {
                Check.Equal(21, candidateCount, "attribute-free gear uses the complete native Dust table");
                return candidateCount - 1;
            });
        Check.Equal(9921u, fallbackResult.Outputs.Single().ItemId, "attribute-free fallback can select Penetration Dust");

        var progression = new (short Quality, short Grade)[]
        {
            (2, 1),
            (2, 2),
            (3, 2),
            (3, 5),
            (13, 25),
            (99, 99)
        };
        var quantities = new List<int>();
        foreach (var (quality, grade) in progression)
        {
            var (kitBag, request) = StageDecomposition(
                (4, Gear(1004, quality, grade, attribute1: 0)));
            var result = GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, kitBag, 30, request, static _ => 0);
            Check.True(result.Committed, $"quality {quality}/grade {grade} decomposition commits");
            quantities.Add(result.Outputs.Single().Quantity);
        }
        Check.True(
            quantities.Zip(quantities.Skip(1), static (lower, higher) => lower <= higher).All(static monotonic => monotonic),
            "decomposition Dust quantity is monotonic across quality and grade");
        Check.Equal(99, quantities[^1], "decomposition Dust output remains capped to one native stack");
    }

    private static void CheckGenericClearSnapshots()
    {
        var correlationNow = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var oneSlotBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            7,
            Material(9900, stack: 99).ToCompactString());
        var oneSlotContext = Context(() => correlationNow, correlationNow);
        Check.True(
            oneSlotContext.Apply(Selection(7, selected: true), oneSlotBag).Status ==
                GearEnhancerSelectionStageStatus.Staged,
            "one-slot native selection stages");
        Check.True(
            oneSlotContext.Apply(Selection(7, selected: false), oneSlotBag).Status ==
                GearEnhancerSelectionStageStatus.Removed,
            "one-slot native control emits its clear event");
        Check.True(
            oneSlotContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MalformedCommit,
                minimumCount: 1,
                maximumCount: 1,
                out var oneSlotSnapshot) &&
            oneSlotSnapshot.Select(static selection => selection.KitBagSlot).SequenceEqual(new[] { 7 }),
            "one-slot clear preserves the authoritative final-action snapshot");
        Check.True(
            oneSlotSnapshot.Single().ExpectedItem == KitBagSlots.GetItem(oneSlotBag, 7),
            "one-slot native staging preserves the exact selected Dust stack");
        var replacedDustBag = KitBagSlots.SetSlot(
            oneSlotBag,
            7,
            Material(9900, stack: 98).ToCompactString());
        var staleDustRequest = new GearMentorRequest(
            GearMentorOperation.MakeAttributeStone,
            [new GearMentorSlotSelection(
                oneSlotSnapshot.Single().KitBagSlot,
                oneSlotSnapshot.Single().ExpectedItem)]);
        AssertRejected(
            GearMentorPlanner.Create(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, replacedDustBag, 200, staleDustRequest),
            replacedDustBag,
            GearMentorStatus.StaleSelection,
            "a replacement Dust stack in a staged native slot is rejected");
        correlationNow += GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
        Check.True(
            !oneSlotContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MalformedCommit,
                minimumCount: 1,
                maximumCount: 1,
                out _),
            "an ordinary one-slot deselection cannot be revived after the short correlation window");

        var threeSlots = new[] { 2, 29, 55 };
        var threeSlotBag = GameDefaults.EmptyKitBag;
        foreach (var slot in threeSlots)
        {
            threeSlotBag = KitBagSlots.SetSlot(
                threeSlotBag,
                slot,
                Gear(1004 + checked((uint)Array.IndexOf(threeSlots, slot)), attribute1: 0).ToCompactString());
        }
        var threeSlotContext = Context();
        foreach (var slot in threeSlots)
        {
            Check.True(
                threeSlotContext.Apply(Selection(slot, selected: true), threeSlotBag).Status ==
                    GearEnhancerSelectionStageStatus.Staged,
                $"three-slot native selection stages bag slot {slot}");
        }
        foreach (var slot in threeSlots)
        {
            Check.True(
                threeSlotContext.Apply(Selection(slot, selected: false), threeSlotBag).Status ==
                    GearEnhancerSelectionStageStatus.Removed,
                $"three-slot native control clears bag slot {slot}");
        }
        Check.True(
            threeSlotContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out var threeSlotSnapshot) &&
            threeSlotSnapshot.Select(static selection => selection.KitBagSlot).SequenceEqual(threeSlots),
            "three-slot clear burst preserves ordered decomposition selections");
        Check.True(
            threeSlotSnapshot.All(selection =>
                selection.ExpectedItem == KitBagSlots.GetItem(threeSlotBag, selection.KitBagSlot)),
            "decomposition clear snapshots preserve every exact selected gear item");

        var partialContext = Context();
        foreach (var slot in threeSlots)
        {
            partialContext.Apply(Selection(slot, selected: true), threeSlotBag);
        }
        partialContext.Apply(Selection(threeSlots[0], selected: false), threeSlotBag);
        Check.True(
            !partialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "an incomplete clear burst cannot commit only its residual decomposition selections");

        var wrongOrderContext = Context();
        foreach (var slot in threeSlots)
        {
            wrongOrderContext.Apply(Selection(slot, selected: true), threeSlotBag);
        }
        foreach (var slot in new[] { threeSlots[1], threeSlots[0], threeSlots[2] })
        {
            wrongOrderContext.Apply(Selection(slot, selected: false), threeSlotBag);
        }
        Check.True(
            !wrongOrderContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "out-of-order native clears cannot rebuild or replay a shorter decomposition snapshot");

        var expiredPartialNow = correlationNow;
        var expiredPartialContext = Context(() => expiredPartialNow, expiredPartialNow);
        foreach (var slot in threeSlots)
        {
            expiredPartialContext.Apply(Selection(slot, selected: true), threeSlotBag);
        }
        expiredPartialContext.Apply(Selection(threeSlots[0], selected: false), threeSlotBag);
        expiredPartialNow += GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
        Check.True(
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "an expired partial clear cannot fall back to residual decomposition selections");

        var emptySelection = Selection(80, selected: true);
        Check.True(
            expiredPartialContext.Apply(emptySelection, threeSlotBag).Status ==
                GearEnhancerSelectionStageStatus.SlotEmpty &&
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "a selected=true packet for an empty slot cannot reset an invalidated clear");
        Check.True(
            expiredPartialContext.Apply(
                Selection(threeSlots[1], selected: true),
                threeSlotBag).Status == GearEnhancerSelectionStageStatus.AlreadyStaged &&
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "a duplicate selected=true packet cannot reset an invalidated clear");
        expiredPartialContext.Apply(Selection(threeSlots[1], selected: false), threeSlotBag);
        expiredPartialContext.Apply(Selection(threeSlots[2], selected: false), threeSlotBag);
        Check.True(
            !expiredPartialContext.TryResolveNativeSlots(
                GearEnhancerSelectionShape.MenuSelection,
                minimumCount: 1,
                maximumCount: 3,
                out _),
            "an expired partial clear cannot rebuild a shorter decomposition snapshot from its suffix");
    }
}
