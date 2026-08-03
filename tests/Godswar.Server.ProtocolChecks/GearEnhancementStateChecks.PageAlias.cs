using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearEnhancementStateChecks
{
    private static void CheckNativePageAliasSelection()
    {
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            Item(4200).ToCompactString());
        kitBag = KitBagSlots.SetSlot(
            kitBag,
            15,
            Item(GearEnhancementMaterialCatalog.FlameSparkItemId).ToCompactString());
        kitBag = KitBagSlots.SetSlot(
            kitBag,
            16,
            Item(1035).ToCompactString());
        kitBag = KitBagSlots.SetSlot(
            kitBag,
            24,
            Item(9980, stack: 99).ToCompactString());

        var now = DateTimeOffset.UtcNow;
        var context = CreatePhysicalContext(now);
        var materials = TestItemContent.Catalog.Materials;

        context.Apply(
            new GearEnhancerItemSelectionPacket(0, 16, true),
            kitBag,
            materials);
        context.Apply(
            new GearEnhancerItemSelectionPacket(0, 15, true),
            kitBag,
            materials);
        var aliasedStone = context.Apply(
            new GearEnhancerItemSelectionPacket(0, 0, true),
            kitBag,
            materials);

        Check.True(
            aliasedStone.Role == GearEnhancerSelectionRole.AttributeStone &&
            aliasedStone.KitBagSlot == 24 &&
            aliasedStone.Item.Id == 9980 &&
            context.AttributeStoneKitBagSlot == 24,
            "stock page-zero alias resolves the unique same-cell Attribute Stone on a later bag page");

        context.Apply(
            new GearEnhancerItemSelectionPacket(0, 16, false),
            kitBag,
            materials);
        context.Apply(
            new GearEnhancerItemSelectionPacket(0, 15, false),
            kitBag,
            materials);
        context.Apply(
            new GearEnhancerItemSelectionPacket(0, 0, false),
            kitBag,
            materials);
        Check.True(
            context.TryResolveNativeCommit(
                GearEnhancerSelectionShape.MalformedCommit,
                out var clearedSelections) &&
            clearedSelections.GearKitBagSlot == 16 &&
            clearedSelections.CatalystKitBagSlot == 15 &&
            clearedSelections.AttributeStoneKitBagSlot == 24,
            "page-zero clear alias preserves the authoritative later-page stone in the native commit triplet");

        var ambiguousBag = KitBagSlots.SetSlot(
            kitBag,
            48,
            Item(9958).ToCompactString());
        var ambiguousContext = CreatePhysicalContext(now);
        ambiguousContext.Apply(
            new GearEnhancerItemSelectionPacket(0, 16, true),
            ambiguousBag,
            materials);
        ambiguousContext.Apply(
            new GearEnhancerItemSelectionPacket(0, 15, true),
            ambiguousBag,
            materials);
        var ambiguous = ambiguousContext.Apply(
            new GearEnhancerItemSelectionPacket(0, 0, true),
            ambiguousBag,
            materials);
        Check.True(
            ambiguous.KitBagSlot == 0 &&
            ambiguous.Item.Id == 4200,
            "ambiguous same-cell Attribute Stones fail closed instead of guessing a bag page");
    }

    private static GearEnhancerSelectionContext CreatePhysicalContext(
        DateTimeOffset now) =>
        new(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2),
            utcNow: () => now);
}
