using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerMovementSpeedStatusChecks
{
    public static void Run()
    {
        var character = new GameCharacter
        {
            Id = 2,
            AccountId = 13,
            Name = "SpeedWireHero",
            Profession = 3,
            Level = 80,
            Equipment = GameDefaults.DefaultEquipment(3)
        };
        var baselineAggregate = ClientStatusAggregate.Empty with
        {
            MovementSpeedMultiplier = 1.42f
        };
        var noMount =
            PlayerMovementSpeedProjection.WithEquippedRidingSpeed(
                TestItemContent.Content.Mounts,
                character,
                baselineAggregate);
        Check.Equal(
            0f,
            noMount.EquippedRidingSpeedBonus,
            "no equipped mount projects zero Riding Speed");

        character.Equipment = EquipmentSlots.SetSlot(
            character.Equipment,
            character.Profession,
            EquipmentSlots.Mount,
            "[16204,,,,,,20,25,1,1,0]");
        Check.True(
            TestItemContent.Content.Mounts.TryGetEquippedRideDefinition(
                character,
                out var equippedMount),
            "speed-wire fixture resolves its equipped mount");
        var aggregate =
            PlayerMovementSpeedProjection.WithEquippedRidingSpeed(
                TestItemContent.Content.Mounts,
                character,
                baselineAggregate);
        Check.Equal(
            equippedMount.SpeedBonus,
            aggregate.EquippedRidingSpeedBonus,
            "Riding Speed projection uses the authoritative quality-aware mount bonus");

        var baselinePacket = PacketBuilder.PlayerStatusUpdate(
            character,
            baselineAggregate);
        var localPacket = PacketBuilder.PlayerStatusUpdate(
            character,
            aggregate);
        Check.Equal(
            1.42f,
            ReadSingle(localPacket, 56),
            "wire offset 56 keeps the current total movement multiplier");
        Check.Equal(
            0f,
            ReadSingle(localPacket, 60),
            "wire offset 60 remains zero so the native NPC interaction identity is not corrupted");
        Check.Equal(
            (byte)0,
            localPacket[62],
            "wire offset 62 keeps the local NPC interaction gate open");
        Check.True(
            localPacket.AsSpan(64, sizeof(float)).SequenceEqual(
                baselinePacket.AsSpan(64, sizeof(float))),
            "Riding Speed extension does not overwrite Credit at wire offset 64");

        const uint remoteObjectId = 0x7135_B24E;
        var remoteBaseline = PacketBuilder.PlayerStatusUpdate(
            character,
            remoteObjectId,
            baselineAggregate);
        var remotePacket = PacketBuilder.PlayerStatusUpdate(
            character,
            remoteObjectId,
            aggregate);
        Check.Equal(
            0f,
            ReadSingle(remotePacket, 60),
            "remote status deliberately does not disclose the local Riding Speed extension");
        Check.True(
            remotePacket.AsSpan(64, sizeof(float)).SequenceEqual(
                remoteBaseline.AsSpan(64, sizeof(float))),
            "remote Riding Speed policy also preserves Credit at wire offset 64");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusUpdate(
                character,
                ClientStatusAggregate.Empty with
                {
                    EquippedRidingSpeedBonus = float.NaN
                }),
            "Riding Speed wire rejects a non-finite value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusUpdate(
                character,
                ClientStatusAggregate.Empty with
                {
                    EquippedRidingSpeedBonus = -0.01f
                }),
            "Riding Speed wire rejects a negative value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusUpdate(
                character,
                ClientStatusAggregate.Empty with
                {
                    EquippedRidingSpeedBonus = 9.01f
                }),
            "Riding Speed wire rejects a value above the movement budget");
    }

    private static float ReadSingle(byte[] packet, int offset) =>
        BinaryPrimitives.ReadSingleLittleEndian(
            packet.AsSpan(offset, sizeof(float)));
}
