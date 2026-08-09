using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private static readonly CompactItemEntry FashionItem =
        CompactItemEntry.Empty with
        {
            Id = 8_068,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1
        };

    private static readonly TransferSlotState FashionEquippedState =
        new(FashionItem, CompactItemEntry.Empty);
    private static readonly TransferSlotState FashionBaggedState =
        new(CompactItemEntry.Empty, FashionItem);

    private static async Task
        CheckCommittedFashionUnequipRestoresAuraAsync()
    {
        var receipt = CreateReceipt(
            EquipmentBagTransferResultStatus.Unequipped,
            FashionEquippedState,
            FashionEquippedState,
            OutboxEventId,
            equipmentSlot: EquipmentSlots.Stylish);
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(receipt),
            liveState: FashionEquippedState,
            persistedState: FashionBaggedState,
            equipmentSlot: EquipmentSlots.Stylish);
        fixture.LiveCharacter.FashionHidden = true;
        fixture.LiveCharacter.EquipmentEffectsVisible = false;

        await InvokeTransferAsync(
            fixture.Handler,
            OperationId,
            equipmentSlot: EquipmentSlots.Stylish);

        Check.True(
            !GameClientHandler.HasEquippedFashion(
                fixture.LiveCharacter),
            "committed Fashion unequip clears native slot 12");
        Check.True(
            !fixture.LiveCharacter.FashionHidden,
            "committed Fashion unequip clears stale Show-off state");
        AssertFashionVisualEffectSequence(
            fixture,
            expectedEffectVisible: true,
            expectedFashionHead: null,
            "committed Fashion unequip");
    }

    private static async Task
        CheckCommittedFashionEquipDefaultsShowAsync()
    {
        var receipt = CreateReceipt(
            EquipmentBagTransferResultStatus.Equipped,
            FashionBaggedState,
            FashionBaggedState,
            OutboxEventId,
            equipmentSlot: EquipmentSlots.Stylish);
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(receipt),
            liveState: FashionBaggedState,
            persistedState: FashionEquippedState,
            equipmentSlot: EquipmentSlots.Stylish);
        fixture.LiveCharacter.FashionHidden = true;
        fixture.LiveCharacter.EquipmentEffectsVisible = true;

        await InvokeTransferAsync(
            fixture.Handler,
            OperationId,
            equipmentSlot: EquipmentSlots.Stylish);

        Check.True(
            GameClientHandler.HasEquippedFashion(
                fixture.LiveCharacter),
            "committed Fashion equip fills native slot 12");
        Check.True(
            !fixture.LiveCharacter.FashionHidden,
            "committed Fashion equip defaults authoritative Show state on");
        AssertFashionVisualEffectSequence(
            fixture,
            expectedEffectVisible: true,
            expectedFashionHead: 8_061u,
            "committed Fashion equip");
    }

    private static void AssertFashionVisualEffectSequence(
        TransferFixture fixture,
        bool expectedEffectVisible,
        uint? expectedFashionHead,
        string description)
    {
        var packets = fixture.Transport.ReadLegacyPackets().ToList();
        var visualIndex = packets.FindIndex(static packet =>
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, sizeof(ushort))) == 0x27D9);
        var effectIndex = packets.FindIndex(static packet =>
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2, sizeof(ushort))) == 0x27DA);

        Check.True(
            visualIndex >= 0,
            $"{description} emits an authoritative visual refresh");
        Check.Equal(
            visualIndex + 1,
            effectIndex,
            $"{description} sends Effect immediately after appearance");
        Check.Equal(
            expectedEffectVisible ? 1u : 0u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[effectIndex].AsSpan(8, sizeof(uint))),
            $"{description} projects the effective aura state");

        if (expectedFashionHead is { } fashionHead)
        {
            Check.Equal(
                fashionHead,
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packets[visualIndex].AsSpan(16, sizeof(uint))),
                $"{description} projects the shown Fashion model");
        }
    }
}
