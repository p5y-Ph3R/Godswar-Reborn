using System.Text.Json;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class GearMentorStateChecks
{
    private static void CheckResultSubIdMapping()
    {
        Check.Equal(
            GearEnhancerProtocol.SelectedItemMissingResultSubId,
            GearEnhancerProtocol.ResolveGearMentorResultSubId(null),
            "missing Gear Mentor result uses selected-item-missing");

        var mappings = new[]
        {
            Map(GearMentorOperation.Decompose, GearMentorStatus.Succeeded, 1005),
            Map(GearMentorOperation.Decompose, GearMentorStatus.SelectionMissing, 1024),
            Map(GearMentorOperation.Decompose, GearMentorStatus.RequestMissing, 1024),
            Map(GearMentorOperation.Decompose, GearMentorStatus.PlayerLevelTooLow, 1015),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InvalidEquipment, 1003),
            Map(GearMentorOperation.Decompose, GearMentorStatus.EquipmentLevelTooLow, 1014),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InsufficientEquipmentQuality, 1004),
            Map(GearMentorOperation.Decompose, GearMentorStatus.ClassSuit, 1032),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InsufficientCapacity, 1020),
            Map(GearMentorOperation.Decompose, GearMentorStatus.StaleSelection, 1002),
            Map(GearMentorOperation.Decompose, GearMentorStatus.InvalidKitBagSlot, 1002),
            Map(GearMentorOperation.Decompose, GearMentorStatus.DuplicateKitBagSlot, 1019),

            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.Succeeded, 1017),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.SelectionMissing, 1025),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.RequestMissing, 1025),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InvalidDust, 1022),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InsufficientDust, 1016),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InsufficientCapacity, 1020),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.StaleSelection, 1002),
            Map(GearMentorOperation.MakeAttributeStone, GearMentorStatus.InvalidKitBagSlot, 1002),

            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.Succeeded, 1823),
            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.InsufficientCapacity, 1020),
            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.InvalidCrystal, 1822),
            Map(GearMentorOperation.TransformCrystal, GearMentorStatus.StaleSelection, 1822),

            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.Succeeded, 304),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.InsufficientGemPieces, 302),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.InsufficientCapacity, 303),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.InvalidGemPieces, 301),
            Map(GearMentorOperation.CombineGemPieces, GearMentorStatus.StaleSelection, 301)
        };

        foreach (var mapping in mappings)
        {
            var result = Result(mapping.Operation, mapping.Status);
            Check.Equal(
                mapping.ExpectedSubId,
                GearEnhancerProtocol.ResolveGearMentorResultSubId(result),
                $"{mapping.Operation}/{mapping.Status} result sub-ID");
        }

        Check.True(
            new[] { 1, 4, 8, 201 }.All(GearEnhancerProtocol.IsGearMentorTransactionSubId),
            "all implemented Gear Mentor action sub-IDs are recognized");
        Check.True(
            new[] { 2, 3, 5, 6, 7, 9 }.All(static subId => !GearEnhancerProtocol.IsGearMentorTransactionSubId(subId)),
            "enhancement, disabled, and combine-navigation sub-IDs are not generic transactions");
        Check.True(
            new[] { 5, 7 }.All(GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId),
            "Instructions and Wash Dust remain the only unavailable menu operations");
        Check.True(
            new[] { 1, 2, 3, 4, 6, 8, 9 }.All(static subId => !GearEnhancerProtocol.IsUnavailableGearMentorMenuSubId(subId)),
            "implemented and navigation menu operations do not return 999");
    }

    private static (string KitBag, GearMentorRequest Request) StageSingle(
        GearMentorOperation operation,
        CompactItemEntry item)
    {
        var kitBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            SingleSelectionSlot,
            item.ToCompactString());
        return (
            kitBag,
            new GearMentorRequest(
                operation,
                [GearMentorSlotSelection.Capture(kitBag, SingleSelectionSlot)]));
    }

    private static (string KitBag, GearMentorRequest Request) StageDecomposition(
        params (int Slot, CompactItemEntry Item)[] items)
    {
        var kitBag = GameDefaults.EmptyKitBag;
        foreach (var (slot, item) in items)
        {
            kitBag = KitBagSlots.SetSlot(kitBag, slot, item.ToCompactString());
        }

        return (
            kitBag,
            new GearMentorRequest(
                GearMentorOperation.Decompose,
                items.Select(item => GearMentorSlotSelection.Capture(kitBag, item.Slot)).ToArray()));
    }

    private static CompactItemEntry Material(
        uint itemId,
        short stack = 1,
        short bound = 0)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 1,
            Grade = 1,
            Bound = bound,
            Stack = stack
        };
    }

    private static CompactItemEntry Gear(
        uint itemId,
        short quality = 2,
        short grade = 1,
        short bound = 0,
        short stack = 1,
        int? attribute1 = null,
        int? attribute2 = null)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Attribute1 = attribute1,
            Attribute2 = attribute2,
            AttributeLevel1 = attribute1.HasValue ? (short)1 : null,
            AttributeLevel2 = attribute2.HasValue ? (short)1 : null,
            Quality = quality,
            Grade = grade,
            Bound = bound,
            Stack = stack
        };
    }

    private static string FillBag(CompactItemEntry filler)
    {
        var kitBag = GameDefaults.EmptyKitBag;
        for (var slot = 0; slot < SlotCount; slot++)
        {
            kitBag = KitBagSlots.SetSlot(kitBag, slot, filler.ToCompactString());
        }

        return kitBag;
    }

    private static int QuantityInBag(string kitBag, uint itemId, short bound)
    {
        return Enumerable.Range(0, SlotCount)
            .Select(slot => KitBagSlots.GetItem(kitBag, slot))
            .Where(item => item.Id == itemId && item.Bound == bound)
            .Sum(static item => item.Stack);
    }

    private static GearEnhancerSelectionContext Context(
        Func<DateTimeOffset>? utcNow = null,
        DateTimeOffset? now = null)
    {
        var createdAt = now ?? DateTimeOffset.UtcNow;
        return new GearEnhancerSelectionContext(
            accountId: 13,
            characterId: 2,
            GearEnhancerProtocol.SpartaEnhancerNpcId,
            GearEnhancerProtocol.DialogIndex,
            operation: null,
            createdAt.AddMinutes(1),
            utcNow);
    }

    private static GearEnhancerItemSelectionPacket Selection(int kitBagSlot, bool selected)
    {
        return new GearEnhancerItemSelectionPacket(
            kitBagSlot / GearEnhancerItemSelectionPacket.SlotsPerPage,
            kitBagSlot % GearEnhancerItemSelectionPacket.SlotsPerPage,
            selected);
    }

    private static void AssertRejected(
        GearMentorResult result,
        string expectedKitBag,
        GearMentorStatus expectedStatus,
        string description)
    {
        Check.True(!result.Committed, description);
        Check.True(result.Status == expectedStatus, $"{description}: status {expectedStatus}");
        Check.Equal(expectedKitBag, result.UpdatedKitBag, $"{description}: bag unchanged");
        Check.Equal(0, result.Mutations.Count, $"{description}: no mutations");
        Check.Equal(0, result.Outputs.Count, $"{description}: no outputs");
    }

    private static ResultSubIdExpectation Map(
        GearMentorOperation operation,
        GearMentorStatus status,
        int expectedSubId)
    {
        return new ResultSubIdExpectation(operation, status, expectedSubId);
    }

    private static GearMentorResult Result(
        GearMentorOperation operation,
        GearMentorStatus status)
    {
        return new GearMentorResult(
            status,
            operation,
            GameDefaults.EmptyKitBag,
            GameDefaults.EmptyKitBag,
            [],
            []);
    }

    private sealed record DustExpectation(
        uint ItemId,
        string NameKey,
        string DisplayName,
        uint StoneItemId,
        string Icon);

    private sealed record PieceExpectation(
        uint ItemId,
        string NameKey,
        string DisplayName,
        string Material,
        string Icon);

    private sealed record TransformExpectation(
        uint SourceItemId,
        uint ResultItemId,
        int Quantity);

    private sealed record PieceRecipeExpectation(
        uint PieceItemId,
        uint GemItemId);

    private sealed record ResultSubIdExpectation(
        GearMentorOperation Operation,
        GearMentorStatus Status,
        int ExpectedSubId);
}
