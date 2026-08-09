using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckPlayerRecoveryProtocolAsync()
    {
        Check.Equal(TimeSpan.FromSeconds(6), GameSessionRegistry.PlayerRecoveryInterval, "modern recovery cadence");
        Check.Equal(63, PlayerRecoveryCatalog.GetBaseHp(1, 0), "level-one warrior base HP recovery");
        Check.Equal(38, PlayerRecoveryCatalog.GetBaseMp(1, 0), "level-one warrior base MP recovery");
        Check.Equal(496, PlayerRecoveryCatalog.GetBaseHp(200, 0), "level-cap warrior base HP recovery");
        Check.Equal(496, PlayerRecoveryCatalog.GetBaseMp(200, 3), "level-cap mage base MP recovery");

        var character = new GameCharacter
        {
            Level = 4,
            Profession = 0,
            CurrentHp = 1_000,
            MaxHp = 1_500,
            CurrentMp = 9,
            MaxMp = 177,
            CalculatedStats = new CharacterStats
            {
                HpRecovery = 10,
                MpRecovery = 5
            }
        };
        Check.True(PlayerRecoveryCatalog.TryApply(character), "living damaged character recovers");
        Check.Equal(1L, character.VitalsRevision, "recovery advances the vitals revision");
        Check.Equal(1_076, character.CurrentHp, "base and bonus HP recovery are added");
        Check.Equal(53, character.CurrentMp, "base and bonus MP recovery are added");
        Check.True(
            PacketBuilder.PlayerVitalsUpdate(0x00001448, character.CurrentHp, character.CurrentMp)
                .SequenceEqual(Convert.FromHexString("10007127481400003404000035000000")),
            "modern absolute HP/MP recovery packet");

        character.CurrentHp = 1_499;
        character.CurrentMp = 176;
        Check.True(PlayerRecoveryCatalog.TryApply(character), "near-full character recovers");
        Check.Equal(2L, character.VitalsRevision, "each changed recovery advances the vitals revision");
        Check.Equal(1_500, character.CurrentHp, "HP recovery clamps to max");
        Check.Equal(177, character.CurrentMp, "MP recovery clamps to max");
        Check.True(!PlayerRecoveryCatalog.TryApply(character), "full character does not produce an update");
        Check.Equal(2L, character.VitalsRevision, "unchanged recovery does not advance the vitals revision");

        character.CurrentHp = 0;
        character.CurrentMp = 1;
        Check.True(!PlayerRecoveryCatalog.TryApply(character), "dead character cannot passively recover");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldSpawnAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x6A17C04D;
        var packet = PacketBuilder.PlayerWorldSpawn(character, objectId);

        Check.Equal(300, packet.Length, "PlayerWorldSpawn packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerWorldSpawn declared length");
        Check.Equal((ushort)0x2725, ReadUInt16(packet, 2), "PlayerWorldSpawn opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "PlayerWorldSpawn object id");
        Check.Equal(character.PositionX, ReadSingle(packet, 60), "PlayerWorldSpawn X at offset 60");
        Check.Equal(character.PositionZ, ReadSingle(packet, 64), "PlayerWorldSpawn Z at offset 64");
        Check.Equal(0f, ReadSingle(packet, 68), "PlayerWorldSpawn terrain-height float at offset 68");
        Check.Equal(1f, ReadSingle(packet, 72), "PlayerWorldSpawn facing at offset 72");
        Check.Equal(character.Face, packet[56], "PlayerWorldSpawn face");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldAppearanceAsync()
    {
        var character = CreateAppearanceCharacter();
        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x613u);

        ReadOnlySpan<byte> expectedVisuals = [0xCA, 0xCA, 0xCA, 0x87, 0x11];
        Check.True(
            packet.AsSpan(81, expectedVisuals.Length).SequenceEqual(expectedVisuals),
            "world visual bytes preserve compact item order and grade/quality nibbles");
        Check.True(
            packet.AsSpan(81 + expectedVisuals.Length, 18 - expectedVisuals.Length).IndexOfAnyExcept((byte)0) < 0,
            "unused world visual bytes are zero");

        ReadOnlySpan<byte> expectedAttributeCounts = [4, 5, 5, 2, 0];
        Check.True(
            packet.AsSpan(102, expectedAttributeCounts.Length).SequenceEqual(expectedAttributeCounts),
            "world item attribute counts preserve compact item order");
        Check.True(
            packet.AsSpan(102 + expectedAttributeCounts.Length, 17 - expectedAttributeCounts.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused world item attribute counts are zero");

        ushort[] expectedIds = [2443, 2261, 1834, 14504, 16184];
        for (var index = 0; index < expectedIds.Length; index++)
        {
            Check.Equal(
                expectedIds[index],
                ReadUInt16(packet, 124 + (index * sizeof(ushort))),
                $"world compact equipment id {index}");
        }

        Check.Equal(0x00108409u, ReadUInt32(packet, 168), "world source-slot equipment mask");

        Check.Equal(0x31585747u, ReadUInt32(packet, 260), "world full-visual extension marker");
        ReadOnlySpan<byte> expectedFullQualities = [10, 10, 10, 7, 1];
        ReadOnlySpan<byte> expectedFullGrades = [12, 12, 12, 8, 1];
        Check.True(
            packet.AsSpan(264, expectedFullQualities.Length).SequenceEqual(expectedFullQualities),
            "world extension preserves full quality values");
        Check.True(
            packet.AsSpan(282, expectedFullGrades.Length).SequenceEqual(expectedFullGrades),
            "world extension preserves full grade values");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldExtendedAppearanceAsync()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", 21).ToArray();
        slots[0] = "[2344,4,80,40,60,240,20,25,1,1,0]";
        slots[8] = "[3246,4,80,240,60,134,20,25,1,1,0]";
        slots[9] = "[3246,4,80,240,60,134,20,25,1,1,0]";
        slots[10] = "[1435,4,80,90,60,230,20,25,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';

        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x814u);

        ReadOnlySpan<byte> expectedLegacyVisuals = [0xCD, 0xCD, 0xCD, 0xCD];
        Check.True(
            packet.AsSpan(81, expectedLegacyVisuals.Length).SequenceEqual(expectedLegacyVisuals),
            "legacy world decoder carries the supported Q13/G12 forge projection");
        Check.Equal(0x31585747u, ReadUInt32(packet, 260), "extended world marker is GWX1");

        ReadOnlySpan<byte> expectedFullQualities = [20, 20, 20, 20];
        ReadOnlySpan<byte> expectedFullGrades = [25, 25, 25, 25];
        Check.True(
            packet.AsSpan(264, expectedFullQualities.Length).SequenceEqual(expectedFullQualities),
            "extended world qualities preserve Q20");
        Check.True(
            packet.AsSpan(282, expectedFullGrades.Length).SequenceEqual(expectedFullGrades),
            "extended world grades preserve G25");
        Check.True(
            packet.AsSpan(264 + expectedFullQualities.Length, 18 - expectedFullQualities.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused extended world quality bytes are zero");
        Check.True(
            packet.AsSpan(282 + expectedFullGrades.Length, 18 - expectedFullGrades.Length)
                .IndexOfAnyExcept((byte)0) < 0,
            "unused extended world grade bytes are zero");

        Check.Equal((ushort)3246, ReadUInt16(packet, 126), "first extended ring remains packed");
        Check.Equal((ushort)3246, ReadUInt16(packet, 128), "second extended ring remains packed");
        Check.Equal((byte)5, packet[102], "extended head keeps its real append-attribute count");
        Check.Equal((byte)5, packet[105], "extended weapon keeps its real append-attribute count");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerWorldMountOverflowPriorityAsync()
    {
        var character = CreateCharacter();
        var slots = Enumerable.Repeat("[]", EquipmentSlots.Mount + 1).ToArray();
        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Stylish; slot++)
        {
            slots[slot] = $"[{1000 + slot},0,0,0,0,0,1,1,1,1,0]";
        }

        slots[EquipmentSlots.MountHead] = "[14500,0,0,0,0,0,1,1,1,1,0]";
        slots[EquipmentSlots.MountArmor] = "[14600,0,0,0,0,0,1,1,1,1,0]";
        slots[EquipmentSlots.MountSoul] = "[14700,0,0,0,0,0,1,1,1,1,0]";
        slots[EquipmentSlots.MountOrnament] = "[14800,0,0,0,0,0,1,1,1,1,0]";
        slots[EquipmentSlots.MountAmulet] = "[14900,0,0,0,0,0,1,1,1,1,0]";
        slots[EquipmentSlots.Mount] = "[14220,0,0,0,0,0,1,1,1,1,0]";
        character.Equipment = string.Join('#', slots) + '#';

        var packet = PacketBuilder.PlayerWorldSpawn(character, 0x815u);
        var equipmentMask = ReadUInt32(packet, 168);
        Check.True(
            (equipmentMask & (1u << EquipmentSlots.Mount)) != 0,
            "fully populated world appearance keeps the ride-defining mount slot");
        Check.True(
            (equipmentMask & (1u << EquipmentSlots.MountAmulet)) == 0,
            "native 18-record overflow drops the least-visible mount-amulet record");
        Check.Equal(
            (ushort)14220,
            ReadUInt16(packet, 124 + (17 * sizeof(ushort))),
            "mount is the final packed native appearance record after overflow prioritization");
        return Task.CompletedTask;
    }

    private static Task CheckRejectedEquipRefreshSlotAsync()
    {
        Check.Equal(
            EquipmentSlots.Mount,
            GameClientHandler.ResolveEquipmentRejectionRefreshSlot(
                requestedEquipmentSlot: -1,
                resolvedEquipmentSlot: EquipmentSlots.Mount),
            "right-click rejection refreshes the inferred authoritative mount slot");
        Check.Equal(
            EquipmentSlots.Armor,
            GameClientHandler.ResolveEquipmentRejectionRefreshSlot(
                requestedEquipmentSlot: EquipmentSlots.Armor,
                resolvedEquipmentSlot: -1),
            "explicit rejection falls back to the client-requested equipment slot");
        Check.Equal(
            -1,
            GameClientHandler.ResolveEquipmentRejectionRefreshSlot(
                requestedEquipmentSlot: -1,
                resolvedEquipmentSlot: -1),
            "unresolved right-click rejection refreshes only the authoritative bag slot");
        return Task.CompletedTask;
    }

    private static Task CheckPlayerAuxiliaryAppearanceAsync()
    {
        var character = CreateAppearanceCharacter();
        const uint objectId = 0x716u;

        var refresh = PacketBuilder.EquipmentVisualRefresh(character, objectId);
        Check.Equal(objectId, ReadUInt32(refresh, 4), "EquipmentVisualRefresh object id");
        Check.Equal((uint)character.Hair, ReadUInt32(refresh, 8), "EquipmentVisualRefresh hair/model");
        Check.Equal((uint)character.Gender + 1u, ReadUInt32(refresh, 12), "EquipmentVisualRefresh one-based gender");
        Check.Equal(2443u, ReadUInt32(refresh, 16), "EquipmentVisualRefresh source slot 0");
        Check.Equal(2261u, ReadUInt32(refresh, 28), "EquipmentVisualRefresh source slot 3");
        Check.Equal(1834u, ReadUInt32(refresh, 56), "EquipmentVisualRefresh source slot 10");

        var extras = PacketBuilder.PlayerAppearanceExtras(character, objectId);
        Check.Equal(objectId, ReadUInt32(extras, 8), "PlayerAppearanceExtras object id");
        Check.Equal((byte)1, extras[64], "PlayerAppearanceExtras neutral presence marker");
        for (var offset = 4; offset < extras.Length; offset++)
        {
            if (offset is >= 8 and < 12 || offset == 64)
            {
                continue;
            }

            Check.Equal((byte)0, extras[offset], $"PlayerAppearanceExtras neutral byte {offset}");
        }

        const uint petId = 4_665;
        var petPresence = PacketBuilder.PetWorldPresence(
            petId,
            objectId);
        Check.Equal(
            (ushort)10248,
            ReadUInt16(petPresence, 2),
            "pet world-presence opcode");
        Check.Equal(
            petId,
            ReadUInt32(petPresence, 4),
            "pet world-presence pet ID");
        Check.Equal(
            objectId,
            ReadUInt32(petPresence, 8),
            "pet world-presence owner object ID");
        Check.Equal(
            (byte)1,
            petPresence[64],
            "pet world-presence captured neutral marker");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetWorldPresence(0, objectId),
            "zero pet ID cannot create a presence packet");

        var title = PacketBuilder.PlayerTitleInfo(character, objectId);
        Check.Equal(objectId, ReadUInt32(title, 4), "PlayerTitleInfo object id");
        Check.True(
            title.AsSpan(8).IndexOfAnyExcept((byte)0) < 0,
            "PlayerTitleInfo untitled body is zero");

        CheckFashionAppearanceProjection();

        return Task.CompletedTask;
    }
}
