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
    private static Task CheckPlayerInspectExtendedSlotsAsync()
    {
        var character = CreateAppearanceCharacter();
        var packet = PacketBuilder.PlayerInspectEquipment(character, 0x817u);
        const int headerLength = 8;
        const int recordLength = 72;
        const int maskOffset = 1520;

        Check.Equal(1524, packet.Length, "inspect packet includes trailing slot mask");
        Check.Equal(2443u, ReadUInt32(packet, headerLength), "inspect packed record 0 is source slot 0");
        Check.Equal(
            2261u,
            ReadUInt32(packet, headerLength + recordLength),
            "inspect packed record 1 skips empty source slots");
        Check.Equal(
            14504u,
            ReadUInt32(packet, headerLength + (3 * recordLength)),
            "inspect packed cosmetic source slot 15 item");
        Check.Equal(
            16184u,
            ReadUInt32(packet, headerLength + (4 * recordLength)),
            "inspect packed title/cosmetic source slot 20 item");
        Check.Equal(
            uint.MaxValue,
            ReadUInt32(packet, headerLength + (5 * recordLength)),
            "first unused inspect record uses empty sentinel");
        Check.Equal(0x00108409u, ReadUInt32(packet, maskOffset), "inspect source-slot mask");

        var detailedSlots = Enumerable.Repeat("[]", 21).ToArray();
        detailedSlots[0] = "[2344,4,80,40,60,240,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        detailedSlots[8] = "[3246,4,80,240,60,134,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        detailedSlots[9] = "[3246,4,80,240,60,134,20,25,1,1,0,710,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        character.Equipment = string.Join('#', detailedSlots) + '#';

        var detailedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x817u);
        Check.Equal(2344u, ReadUInt32(detailedPacket, headerLength), "inspect detailed head item");
        Check.Equal((byte)20, detailedPacket[headerLength + 24], "inspect preserves Q20");
        Check.Equal((byte)25, detailedPacket[headerLength + 25], "inspect preserves G25");
        var expectedAttributes = new[] { 4, 80, 40, 60, 240 };
        for (var attribute = 0; attribute < expectedAttributes.Length; attribute++)
        {
            Check.Equal(
                expectedAttributes[attribute],
                ReadInt32(detailedPacket, headerLength + 4 + (attribute * 4)),
                $"inspect preserves append attribute {attribute + 1}");
        }

        Check.Equal((ushort)710, ReadUInt16(detailedPacket, headerLength + 32), "inspect preserves holy suit code");
        Check.Equal((ushort)4, ReadUInt16(detailedPacket, headerLength + 34), "inspect preserves socket count");
        var expectedStoneCodes = new ushort[] { 109, 509, 709, 309 };
        var expectedStoneValues = new ushort[] { 1400, 2200, 1200, 1400 };
        for (var stone = 0; stone < expectedStoneCodes.Length; stone++)
        {
            Check.Equal(
                expectedStoneCodes[stone],
                ReadUInt16(detailedPacket, headerLength + 36 + (stone * 2)),
                $"inspect preserves holy-stone code {stone + 1}");
            Check.Equal(
                expectedStoneValues[stone],
                ReadUInt16(detailedPacket, headerLength + 44 + (stone * 2)),
                $"inspect preserves holy-stone value {stone + 1}");
        }

        Check.Equal(3246u, ReadUInt32(detailedPacket, headerLength + recordLength), "inspect first ring record");
        Check.Equal(3246u, ReadUInt32(detailedPacket, headerLength + (2 * recordLength)), "inspect second ring record");
        var expectedRingAttributes = new[] { 4, 80, 240, 60, 134 };
        for (var attribute = 0; attribute < expectedRingAttributes.Length; attribute++)
        {
            Check.Equal(
                expectedRingAttributes[attribute],
                ReadInt32(
                    detailedPacket,
                    headerLength + (2 * recordLength) + 4 + (attribute * 4)),
                $"inspect second ring preserves append attribute {attribute + 1}");
        }

        Check.Equal(
            expectedStoneCodes[3],
            ReadUInt16(detailedPacket, headerLength + (2 * recordLength) + 42),
            "inspect second ring preserves fourth holy stone");
        Check.Equal(0x00000301u, ReadUInt32(detailedPacket, maskOffset), "inspect distinguishes both ring slots");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + recordLength + 64)
                != ReadUInt32(detailedPacket, headerLength + (2 * recordLength) + 64),
            "identical ring types have distinct item state identities");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + recordLength + 68)
                != ReadUInt32(detailedPacket, headerLength + (2 * recordLength) + 68),
            "identical ring types have distinct item slot identities");

        var repeatedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x818u);
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 64),
            ReadUInt32(repeatedPacket, headerLength + 64),
            "inspect item state identity is stable");
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 68),
            ReadUInt32(repeatedPacket, headerLength + 68),
            "inspect item slot identity is stable");

        var otherCharacter = CreateAppearanceCharacter();
        otherCharacter.Id = character.Id + 1;
        otherCharacter.Equipment = character.Equipment;
        var otherPacket = PacketBuilder.PlayerInspectEquipment(otherCharacter, 0x819u);
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 64) != ReadUInt32(otherPacket, headerLength + 64),
            "inspect item state identity is character-specific");
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 68) != ReadUInt32(otherPacket, headerLength + 68),
            "inspect item slot identity is character-specific");

        detailedSlots[0] = "[2344,4,80,40,60,240,20,25,1,1,0,711,5,5,5,5,5,4,1,10,5,10,7,10,3,10]";
        character.Equipment = string.Join('#', detailedSlots) + '#';
        var upgradedPacket = PacketBuilder.PlayerInspectEquipment(character, 0x81Au);
        Check.True(
            ReadUInt32(detailedPacket, headerLength + 64) != ReadUInt32(upgradedPacket, headerLength + 64),
            "inspect item state identity changes with item metadata");
        Check.Equal(
            ReadUInt32(detailedPacket, headerLength + 68),
            ReadUInt32(upgradedPacket, headerLength + 68),
            "inspect item slot identity survives item metadata changes");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerStatusEffectsAsync()
    {
        var character = CreateCharacter();
        const uint objectId = 0x7135B24E;
        var effects = new ClientStatusEffect[]
        {
            new(1504, 43_200),
            new(511, 28_800),
            new(1503, uint.MaxValue),
            new(586, 28_800)
        };
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            objectId,
            effects,
            new ClientStatusAggregate(0, 0, 6.2f));

        Check.Equal(340, packet.Length, "status-effect packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "status-effect declared length");
        Check.Equal((ushort)10167, ReadUInt16(packet, 2), "status-effect opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "status-effect object id");
        Check.Equal(4u, ReadUInt32(packet, 8), "status-effect count");

        // Preserved MSG_STATUS writes std::map entries in ascending status-ID order.
        Check.Equal(511u, ReadUInt32(packet, 12), "first sorted status ID");
        Check.Equal(586u, ReadUInt32(packet, 16), "second sorted status ID");
        Check.Equal(1503u, ReadUInt32(packet, 20), "third sorted status ID");
        Check.Equal(1504u, ReadUInt32(packet, 24), "fourth sorted status ID");
        Check.Equal(28_800u, ReadUInt32(packet, 92), "first status remaining time");
        Check.Equal(28_800u, ReadUInt32(packet, 96), "second status remaining time");
        Check.Equal(uint.MaxValue, ReadUInt32(packet, 100), "permanent status remaining-time sentinel");
        Check.Equal(43_200u, ReadUInt32(packet, 104), "area status remaining time");
        Check.Equal(0u, ReadUInt32(packet, 28), "unused status ID slot remains zero");
        Check.Equal(0u, ReadUInt32(packet, 108), "unused status time slot remains zero");
        Check.Equal(character.MaxHp, ReadInt32(packet, 172), "full StatusData max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 176), "full StatusData max MP");
        Check.Equal(PlayerRecoveryCatalog.GetTotalHp(character), ReadInt32(packet, 180), "full StatusData HP recovery");
        Check.Equal(PlayerRecoveryCatalog.GetTotalMp(character), ReadInt32(packet, 184), "full StatusData MP recovery");
        Check.Equal(character.CalculatedStats!.PhysicalAttack, ReadInt32(packet, 188), "full StatusData physical attack");
        Check.Equal(character.CalculatedStats.PhysicalDefense, ReadInt32(packet, 192), "full StatusData physical defense");
        Check.Equal(character.CalculatedStats.MagicAttack, ReadInt32(packet, 196), "full StatusData magic attack");
        Check.Equal(character.CalculatedStats.MagicDefense, ReadInt32(packet, 200), "full StatusData magic defense");
        Check.Equal(character.CalculatedStats.Hit, ReadInt32(packet, 204), "full StatusData hit");
        Check.Equal(character.CalculatedStats.Dodge, ReadInt32(packet, 208), "full StatusData dodge");
        Check.Equal(character.CalculatedStats.Critical, ReadInt32(packet, 212), "full StatusData critical");
        Check.Equal(character.CalculatedStats.CriticalResistance, ReadInt32(packet, 216), "full StatusData critical resistance");
        Check.Equal(0.1234f, ReadSingle(packet, 220), "full StatusData physical damage bonus");
        Check.Equal(0.2345f, ReadSingle(packet, 224), "full StatusData magic damage bonus");
        Check.Equal(character.CalculatedStats.DamageAbsorb, ReadInt32(packet, 228), "full StatusData damage absorb");
        Check.Equal(0.3456f, ReadSingle(packet, 232), "full StatusData received-cure bonus");
        Check.Equal(0.4567f, ReadSingle(packet, 236), "full StatusData cure bonus");
        Check.Equal(0u, ReadUInt32(packet, 240), "unimplemented status-hit field remains zero");
        Check.Equal(6.2f, ReadSingle(packet, 300), "status aggregate fighter-EXP bonus");
        Check.Equal(1f, ReadSingle(packet, 324), "status movement-speed baseline");
        Check.Equal(0u, ReadUInt32(packet, 336), "unused final StatusData field remains zero");

        var localPacket = PacketBuilder.PlayerStatusEffects(
            character,
            [],
            ClientStatusAggregate.Empty);
        Check.Equal(0x1448u, ReadUInt32(localPacket, 4), "status-effect local player object ID");
        Check.Equal(0u, ReadUInt32(localPacket, 8), "empty status-effect count");

        var bootstrapPacket = PacketBuilder.PlayerExtendedStatus(character);
        Check.Equal(340, bootstrapPacket.Length, "legacy extended-status entry point uses canonical length");
        Check.Equal((ushort)10167, ReadUInt16(bootstrapPacket, 2), "legacy extended-status entry point uses canonical opcode");
        Check.Equal(character.MaxHp, ReadInt32(bootstrapPacket, 172), "legacy extended-status entry point includes full data");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusEffects(
                character,
                Enumerable.Range(1, 21)
                    .Select(static id => new ClientStatusEffect((uint)id, 1))
                    .ToArray(),
                ClientStatusAggregate.Empty),
            "status-effect packet rejects more than twenty entries");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PlayerStatusEffects(
                character,
                [],
                new ClientStatusAggregate(0, 0, float.NaN)),
            "status-effect packet rejects non-finite aggregate EXP");

        return Task.CompletedTask;
    }
}
