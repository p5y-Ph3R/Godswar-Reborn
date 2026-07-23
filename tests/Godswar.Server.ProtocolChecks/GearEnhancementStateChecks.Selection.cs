using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearEnhancementStateChecks
{
    private static void CheckNativeItemSelection()
    {
        var kitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 0, (Item(1000) with
        {
            Attribute1 = 0,
            Attribute2 = 40,
            Attribute3 = 60,
            Attribute4 = 80
        }).ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, Item(9960, stack: 99).ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 12, Item(9930, stack: 99).ToCompactString());

        var gearPacket = NativeSelectionPacket(pageSlot: 0, selected: true, scratch: 0x579601);
        var quartzPacket = NativeSelectionPacket(pageSlot: 1, selected: true, scratch: 0x577501);
        var stonePacket = NativeSelectionPacket(pageSlot: 12, selected: true, scratch: 0x577501);
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(gearPacket, out var gearSelection),
            "native Gear Mentor selection accepts its unstable scratch tail");
        Check.Equal(0, gearSelection.KitBagSlot, "native Gear Mentor decodes bag slot 0");
        Check.True(gearSelection.Selected, "native Gear Mentor decodes selected flag");

        var now = DateTimeOffset.UtcNow;
        var context = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: GearEnhancementOperation.Enhance,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        Check.True(
            context.Apply(gearSelection, kitBag).Role == GearEnhancerSelectionRole.Gear,
            "first native selection is the gear control");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(quartzPacket, out var quartzSelection),
            "native Quartz selection parses");
        Check.True(
            context.Apply(quartzSelection, kitBag).Role == GearEnhancerSelectionRole.Catalyst,
            "second native selection is the operation catalyst control");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(stonePacket, out var stoneSelection),
            "native Attribute Stone selection parses");
        Check.True(
            context.Apply(stoneSelection, kitBag).Role == GearEnhancerSelectionRole.AttributeStone,
            "third native selection is the Attribute Stone control");
        Check.Equal(0, context.GearKitBagSlot, "native context stages gear slot 0");
        Check.Equal(1, context.CatalystKitBagSlot, "native context stages Quartz slot 1");
        Check.Equal(12, context.AttributeStoneKitBagSlot, "native context stages Strength Stone slot 12");
        var scratchArgs = Enumerable.Repeat(
                -1,
                GearEnhancerProtocol.FunctionActionArgumentCount)
            .ToArray();
        scratchArgs[GearEnhancerProtocol.GearArgumentIndex] = 0x0CA589;
        var scratchShape = GearEnhancerProtocol.ReadSelection(
            scratchArgs,
            out _,
            out _,
            out _);
        Check.True(
            scratchShape == GearEnhancerSelectionShape.MalformedCommit,
            "native final-action scratch can look like a malformed inline commit");
        Check.True(
            context.TryResolveNativeCommit(
                scratchShape,
                out var stagedSelections) &&
            stagedSelections.GearKitBagSlot == 0 &&
            stagedSelections.CatalystKitBagSlot == 1 &&
            stagedSelections.AttributeStoneKitBagSlot == 12,
            "native 10193 staging overrides harmless malformed final-action scratch");
        Check.True(
            stagedSelections.Gear.ExpectedItem == KitBagSlots.GetItem(kitBag, 0) &&
            stagedSelections.Catalyst.ExpectedItem == KitBagSlots.GetItem(kitBag, 1) &&
            stagedSelections.AttributeStone.ExpectedItem == KitBagSlots.GetItem(kitBag, 12),
            "native staging retains the exact selected item snapshots");
        var replacedGearBag = KitBagSlots.SetSlot(
            kitBag,
            stagedSelections.GearKitBagSlot,
            Item(1004).ToCompactString());
        var replacedGearRequest = new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            new GearEnhancementSlotSelection(
                stagedSelections.Gear.KitBagSlot,
                stagedSelections.Gear.ExpectedItem),
            new GearEnhancementSlotSelection(
                stagedSelections.AttributeStone.KitBagSlot,
                stagedSelections.AttributeStone.ExpectedItem),
            new GearEnhancementSlotSelection(
                stagedSelections.Catalyst.KitBagSlot,
                stagedSelections.Catalyst.ExpectedItem));
        CheckRejectedUnchanged(
            GearEnhancementPlanner.Create(replacedGearBag, replacedGearRequest),
            replacedGearBag,
            GearEnhancementStatus.StaleSelection,
            "a replacement item in a staged native slot is never silently enhanced");
        Check.True(
            context.IsActiveFor(
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "native selection context is bound to account, character, NPC, dialog, and operation");
        Check.True(
            !context.IsActiveFor(
                13,
                3,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "native selection context cannot cross characters");

        var stockPhysicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        Check.True(
            stockPhysicalContext.IsActiveFor(
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now) &&
            stockPhysicalContext.IsActiveFor(
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Add,
                now),
            "physical initial-menu context binds its operation only from final 10069");
        stockPhysicalContext.Apply(gearSelection, kitBag);
        stockPhysicalContext.Apply(quartzSelection, kitBag);
        stockPhysicalContext.Apply(stoneSelection, kitBag);
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(
                NativeSelectionPacket(pageSlot: 0, selected: false, scratch: 0x22B92D00),
                out var clearGear),
            "observed native gear confirmation-clear packet parses");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(
                NativeSelectionPacket(pageSlot: 1, selected: false, scratch: 0x22B92D00),
                out var clearQuartz),
            "observed native Quartz confirmation-clear packet parses");
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(
                NativeSelectionPacket(pageSlot: 12, selected: false, scratch: 0x22B92D00),
                out var clearStone),
            "observed native Attribute Stone confirmation-clear packet parses");
        stockPhysicalContext.Apply(clearGear, kitBag);
        stockPhysicalContext.Apply(clearQuartz, kitBag);
        stockPhysicalContext.Apply(clearStone, kitBag);
        Check.True(
            stockPhysicalContext.GearKitBagSlot == -1 &&
            stockPhysicalContext.CatalystKitBagSlot == -1 &&
            stockPhysicalContext.AttributeStoneKitBagSlot == -1,
            "native Start visually clears all three active controls");
        Check.True(
            stockPhysicalContext.TryResolveNativeCommit(
                scratchShape,
                out var clearedSelections) &&
            clearedSelections.GearKitBagSlot == 0 &&
            clearedSelections.CatalystKitBagSlot == 1 &&
            clearedSelections.AttributeStoneKitBagSlot == 12,
            "observed select-then-clear-then-final sequence retains the complete commit triplet");

        var correlationNow = now;
        var expiredPhysicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => correlationNow);
        expiredPhysicalContext.Apply(gearSelection, kitBag);
        expiredPhysicalContext.Apply(quartzSelection, kitBag);
        expiredPhysicalContext.Apply(stoneSelection, kitBag);
        expiredPhysicalContext.Apply(clearGear, kitBag);
        expiredPhysicalContext.Apply(clearQuartz, kitBag);
        expiredPhysicalContext.Apply(clearStone, kitBag);
        correlationNow += GearEnhancerProtocol.NativeClearCommitCorrelationLifetime;
        Check.True(
            !expiredPhysicalContext.TryResolveNativeCommit(scratchShape, out _),
            "a completed native clear triplet cannot be revived after its short correlation window");

        var incompletePhysicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        incompletePhysicalContext.Apply(gearSelection, kitBag);
        incompletePhysicalContext.Apply(quartzSelection, kitBag);
        Check.True(
            !incompletePhysicalContext.TryResolveNativeCommit(
                scratchShape,
                out _),
            "an unbound initial-menu context cannot turn an incomplete selection into a commit");

        var request = new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            GearEnhancementSlotSelection.Capture(kitBag, context.GearKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, context.AttributeStoneKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, context.CatalystKitBagSlot));
        var exactAttempt = GearEnhancementPlanner.Create(kitBag, request);
        Check.True(
            exactAttempt.Committed,
            $"the observed Short Sword + QP1 + Strength Stone attempt succeeds ({exactAttempt.RejectionReason})");
        Check.Equal(1, exactAttempt.EquipmentAfter.Attribute1 ?? -1, "observed Strength attribute advances to template 1");
        Check.Equal((short)2, exactAttempt.EquipmentAfter.AttributeLevel1 ?? 0, "observed Strength attribute advances to level 2");

        var wrongOrder = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: GearEnhancementOperation.Enhance,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
        wrongOrder.Apply(gearSelection, kitBag);
        wrongOrder.Apply(stoneSelection, kitBag);
        wrongOrder.Apply(quartzSelection, kitBag);
        var wrongOrderRequest = new GearEnhancementRequest(
            GearEnhancementOperation.Enhance,
            GearEnhancementSlotSelection.Capture(kitBag, wrongOrder.GearKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, wrongOrder.AttributeStoneKitBagSlot),
            GearEnhancementSlotSelection.Capture(kitBag, wrongOrder.CatalystKitBagSlot));
        var wrongOrderResult = GearEnhancementPlanner.Create(kitBag, wrongOrderRequest);
        CheckRejectedUnchanged(
            wrongOrderResult,
            kitBag,
            GearEnhancementStatus.InvalidAttributeStone,
            "native second/third controls reject a Stone and Quartz placed in the wrong order");
        Check.Equal(
            GearEnhancerProtocol.MissingAttributeStoneResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                wrongOrderResult,
                wrongOrderRequest),
            "wrong native Attribute Stone control receives the specific third-slot error");

        var removalPacket = NativeSelectionPacket(pageSlot: 1, selected: false, scratch: 0x0CA58900);
        Check.True(
            GearEnhancerItemSelectionPacket.TryParse(removalPacket, out var removal),
            "native removal packet ignores scratch tail");
        var removalResult = context.Apply(removal, kitBag);
        Check.True(
            removalResult.Status == GearEnhancerSelectionStageStatus.Removed,
            "native removal clears its staged role");
        Check.Equal(-1, context.CatalystKitBagSlot, "native removal clears only the catalyst control");
        Check.True(
            !context.TryResolveNativeCommit(
                scratchShape,
                out _),
            "a normal single-slot removal cannot reuse the previously complete triplet");

        var malformed = NativeSelectionPacket(pageSlot: 1, selected: true, scratch: 0);
        malformed[8] = 2;
        Check.True(
            !GearEnhancerItemSelectionPacket.TryParse(malformed, out _),
            "native selection rejects flags other than selected/removed");
    }
}
