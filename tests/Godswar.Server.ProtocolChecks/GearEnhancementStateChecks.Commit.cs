using System.Buffers.Binary;
using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearEnhancementStateChecks
{
    private static void CheckCommitContextGuards()
    {
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var originContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.OriginDialogIndex,
            operation: GearEnhancementOperation.Enhance,
            expiresAt: now.AddMinutes(2));
        Check.True(
            GameClientHandler.GearEnhancerCommitContextMatches(
                originContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "Origin Enhancer inline commits retain their live operation-bound page context");
        Check.True(
            !GameClientHandler.GearEnhancerCommitContextMatches(
                null,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Enhance,
                now) &&
            !GameClientHandler.GearEnhancerCommitContextMatches(
                originContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Add,
                now) &&
            !GameClientHandler.GearEnhancerCommitContextMatches(
                originContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaOriginEnhancerNpcId,
                GearEnhancerProtocol.OriginDialogIndex,
                GearEnhancementOperation.Enhance,
                originContext.ExpiresAt),
            "inline enhancement commits reject missing, mismatched, and expired contexts");

        var physicalContext = new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            npcId: GearEnhancerProtocol.SpartaEnhancerNpcId,
            dialogIndex: GearEnhancerProtocol.DialogIndex,
            operation: null,
            expiresAt: now.AddMinutes(2));
        Check.True(
            GameClientHandler.GearEnhancerCommitContextMatches(
                physicalContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Add,
                now) &&
            GameClientHandler.GearEnhancerCommitContextMatches(
                physicalContext,
                gearMentorOperationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Delete,
                now),
            "physical Gear Mentor keeps its client-local Add/Enhance/Delete final-operation binding");
        Check.True(
            !GameClientHandler.GearEnhancerCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.DecomposeGearSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DialogIndex,
                GearEnhancementOperation.Enhance,
                now),
            "a Gear Mentor transaction page cannot be reused to commit Add/Enhance/Delete");

        Check.True(
            GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                operationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                now) &&
            GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                operationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.MakeAttributeStoneSubId,
                now) &&
            GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                operationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.TransformCrystalSubId,
                now),
            "client-local Decompose, Make Stone, and Transform actions commit from the initial-menu context");
        Check.True(
            !GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.DecomposeGearSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                now) &&
            !GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                operationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.AthensEnhancerNpcId,
                GearEnhancerProtocol.DecomposeGearSubId,
                now),
            "client-local Gear Mentor commits reject a forged page marker or physical NPC");
        Check.True(
            GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                GearEnhancerProtocol.CombineGemPiecesActionSubId,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.CombineGemPiecesActionSubId,
                now) &&
            !GameClientHandler.GearMentorCommitContextMatches(
                physicalContext,
                operationPageSubId: null,
                13,
                2,
                GearEnhancerProtocol.SpartaEnhancerNpcId,
                GearEnhancerProtocol.CombineGemPiecesActionSubId,
                now),
            "gem-piece action 201 is accepted only through its matching page marker");
        Check.True(
            GameClientHandler.IsCombineGemPiecesConfirmAlias(
                GearEnhancerProtocol.CombineGemPiecesMenuSubId,
                GearEnhancerProtocol.CombineGemPiecesActionSubId),
            "stock menu action 9 is normalized to the gem-piece commit only from page 201");
        Check.True(
            !GameClientHandler.IsCombineGemPiecesConfirmAlias(
                GearEnhancerProtocol.CombineGemPiecesMenuSubId,
                operationPageSubId: null) &&
            !GameClientHandler.IsCombineGemPiecesConfirmAlias(
                GearEnhancerProtocol.CombineGemPiecesActionSubId,
                GearEnhancerProtocol.CombineGemPiecesActionSubId),
            "initial menu action 9 still opens the page and other actions are not aliased");
    }

    private static void CheckPreciseNativeResultMapping()
    {
        var equipment = Item(1000);
        var baseResult = new GearEnhancementResult(
            GearEnhancementStatus.StaleSelection,
            GearEnhancementOperation.Enhance,
            GameDefaults.EmptyKitBag,
            GameDefaults.EmptyKitBag,
            equipment,
            equipment,
            [],
            "A staged item changed.");

        Check.Equal(
            GearEnhancerProtocol.SelectedItemMissingResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(GearEnhancementOperation.Enhance, baseResult),
            "stale native selection maps to chosen-item-missing instead of generic 1019");
        Check.Equal(
            GearEnhancerProtocol.MissingGearResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                baseResult with { Status = GearEnhancementStatus.UnsupportedEquipment }),
            "unsupported gear maps to the first-slot gear error");
        Check.Equal(
            GearEnhancerProtocol.QuartzLevelMismatchResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                baseResult with { Status = GearEnhancementStatus.AttributeLevelMismatch }),
            "stored/template attribute level mismatch maps to the native level error");
        Check.Equal(
            GearEnhancerProtocol.AttributeNotEnhanceableResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Enhance,
                baseResult with { Status = GearEnhancementStatus.AttributeAmbiguous }),
            "ambiguous Enhance attribute maps to cannot-enhance instead of generic 1019");
        Check.Equal(
            GearEnhancerProtocol.MissingDeleteAttributeResultSubId,
            GearEnhancerProtocol.ResolveResultSubId(
                GearEnhancementOperation.Delete,
                baseResult with { Status = GearEnhancementStatus.AttributeAmbiguous }),
            "ambiguous Delete attribute maps to the native matching-attribute error");
    }

    private static byte[] NativeSelectionPacket(int pageSlot, bool selected, int scratch)
    {
        var payload = new byte[GearEnhancerItemSelectionPacket.PayloadLength];
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4, 4), pageSlot);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8, 4), scratch);
        payload[8] = selected ? (byte)1 : (byte)0;
        return payload;
    }

    private static void CheckRejectedUnchanged(
        GearEnhancementResult result,
        string expectedKitBag,
        GearEnhancementStatus expectedStatus,
        string description)
    {
        Check.True(!result.Committed && result.Status == expectedStatus, description);
        Check.Equal(expectedKitBag, result.UpdatedKitBag, $"{description}: kit bag is unchanged");
        Check.Equal(0, result.Mutations.Count, $"{description}: no mutations are emitted");
    }

    private const int GearSlot = 10;
    private const int StoneSlot = 11;
    private const int CatalystSlot = 12;

    private static (string KitBag, GearEnhancementRequest Request) Stage(
        GearEnhancementOperation operation,
        CompactItemEntry gear,
        CompactItemEntry stone,
        CompactItemEntry catalyst)
    {
        var kitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, GearSlot, gear.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, StoneSlot, stone.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, CatalystSlot, catalyst.ToCompactString());
        return (
            kitBag,
            new GearEnhancementRequest(
                operation,
                GearEnhancementSlotSelection.Capture(kitBag, GearSlot),
                GearEnhancementSlotSelection.Capture(kitBag, StoneSlot),
                GearEnhancementSlotSelection.Capture(kitBag, CatalystSlot)));
    }

    private static CompactItemEntry Item(
        uint itemId,
        short stack = 1,
        short bound = 0)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Stack = stack,
            Bound = bound
        };
    }
}
