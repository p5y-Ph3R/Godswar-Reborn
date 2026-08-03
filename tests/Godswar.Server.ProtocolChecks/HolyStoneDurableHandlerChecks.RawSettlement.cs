using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckRawSettlementOrderingAsync()
    {
        await CheckRawStackDecrementSettlementAsync();
        await CheckRawBagTargetSettlementAsync();
        await CheckRawDrillBagSettlementAsync();
    }

    private static async Task CheckRawStackDecrementSettlementAsync()
    {
        var material = CreateFireSpirit(stack: 3);
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            7,
            material.ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            WeaponSlot,
            WeaponBefore.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            bag,
            character =>
            {
                character.KitBag = KitBagSlots.SetSlot(
                    character.KitBag,
                    7,
                    (material with
                    {
                        Stack = 2
                    }).ToCompactString());
                return character;
            });

        await InvokeAsync(
            fixture.Handler,
            CreateRawCanonicalMountPacket(
                HolyStoneProtocol.EncodeKitBagReference(WeaponSlot),
                HolyStoneProtocol.EncodeKitBagReference(7)));

        AssertRawSettlement(
            fixture,
            expectedChangedSlots: [7],
            "raw stack decrement");
    }

    private static async Task CheckRawBagTargetSettlementAsync()
    {
        var target = WeaponBefore with
        {
            SocketCount = 1
        };
        var material = CreateFireSpirit(stack: 1);
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            7,
            material.ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            15,
            target.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            bag,
            character =>
            {
                character.KitBag = KitBagSlots.ClearSlot(
                    character.KitBag,
                    7);
                character.KitBag = KitBagSlots.SetSlot(
                    character.KitBag,
                    15,
                    (target with
                    {
                        Socket1EffectId = 1,
                        Socket1Level = 4
                    }).ToCompactString());
                return character;
            });

        await InvokeAsync(
            fixture.Handler,
            CreateRawCanonicalMountPacket(
                HolyStoneProtocol.EncodeKitBagReference(15),
                HolyStoneProtocol.EncodeKitBagReference(7)));

        AssertRawSettlement(
            fixture,
            expectedChangedSlots: [7, 15],
            "raw bag-target Mount");
    }

    private static async Task CheckRawDrillBagSettlementAsync()
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            WeaponSlot,
            WeaponBefore.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            bag,
            character =>
            {
                var weapon = KitBagSlots.GetItem(
                    character.KitBag,
                    WeaponSlot);
                character.KitBag = KitBagSlots.SetSlot(
                    character.KitBag,
                    WeaponSlot,
                    (weapon with
                    {
                        SocketCount = 2
                    }).ToCompactString());
                return character;
            });
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.DrillSubId,
            args => args[HolyStoneProtocol.TargetArgumentIndex] =
                16);

        await InvokeAsync(fixture.Handler, packet);

        AssertRawSettlement(
            fixture,
            expectedChangedSlots: [WeaponSlot],
            "raw bag-target Drill");
    }

    private static void AssertRawSettlement(
        RawHolyStoneFixture fixture,
        int[] expectedChangedSlots,
        string description)
    {
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Count > 2,
            $"{description} emits authoritative rehydration");
        Check.Equal(
            Opcodes.NpcFunctionActionResponse,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packets[0].AsSpan(2, sizeof(ushort))),
            $"{description} sends stock result first");

        var acknowledgements = packets
            .Select((packet, index) => (packet, index))
            .Where(entry =>
                IsKitBagDeletionAcknowledgement(entry.packet))
            .ToArray();
        Check.Equal(
            expectedChangedSlots.Length,
            acknowledgements.Length,
            $"{description} changed-slot clear count");
        var actualSlots = acknowledgements
            .Select(entry =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    entry.packet.AsSpan(8, sizeof(ushort))) * 24 +
                BinaryPrimitives.ReadUInt16LittleEndian(
                    entry.packet.AsSpan(10, sizeof(ushort))))
            .ToArray();
        Check.True(
            actualSlots.SequenceEqual(expectedChangedSlots),
            $"{description} clears exact changed slots");

        var firstHydrationIndex = Array.FindIndex(
            packets.ToArray(),
            packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packet.AsSpan(2, sizeof(ushort))) is
                    0x27B6 or 0x27D9 or 0x2731 or 0x2748);
        Check.True(
            firstHydrationIndex > 0,
            $"{description} contains authoritative snapshots");
        Check.True(
            acknowledgements.All(entry =>
                entry.index > 0 &&
                entry.index < firstHydrationIndex),
            $"{description} clears changed slots before rehydration");
    }
}
