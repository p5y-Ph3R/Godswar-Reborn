using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckPlayerDetailAsync()
    {
        var character = CreateCharacter();
        character.Silver = 38_832;
        character.Gold = 6;
        character.MedusaHonorPoints = 2_025;
        character.CurrentMap = 200;
        var packet = PacketBuilder.PlayerDetail(character);

        Check.Equal(136, packet.Length, "PlayerDetail packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerDetail declared length");
        Check.Equal((ushort)0x273B, ReadUInt16(packet, 2), "PlayerDetail opcode");
        Check.Equal(character.Name, ReadFixedAscii(packet, 4, 32), "PlayerDetail character name");
        Check.Equal(
            (ushort)character.CurrentMap,
            ReadUInt16(packet, 38),
            "PlayerDetail current map");
        Check.Equal(character.Level, ReadInt32(packet, 96), "PlayerDetail level");
        Check.Equal(character.MaxHp, ReadInt32(packet, 100), "PlayerDetail max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 104), "PlayerDetail max MP");
        Check.Equal(character.CurrentHp, ReadInt32(packet, 108), "PlayerDetail current HP");
        Check.Equal(character.CurrentMp, ReadInt32(packet, 112), "PlayerDetail current MP");
        Check.Equal(character.Silver, ReadInt32(packet, 116), "PlayerDetail captured silver field");
        Check.Equal(character.Gold, ReadInt32(packet, 120), "PlayerDetail captured gold field");
        Check.Equal(character.MedusaHonorPoints, ReadInt32(packet, 124), "PlayerDetail Medusa Honor field");

        return Task.CompletedTask;
    }

    private static Task CheckPlayerStatusUpdateAsync()
    {
        PlayerMovementSpeedStatusChecks.Run();
        PlayerRemoteStatusProjectionChecks.Run();
        var character = CreateCharacter();
        character.MedusaHonorPoints = 2_025;
        character.CurrentMap = 200;
        const uint objectId = 0x7135B24E;
        var packet = PacketBuilder.PlayerStatusUpdate(character, objectId);

        Check.Equal(236, packet.Length, "PlayerStatusUpdate packet length");
        Check.Equal((ushort)packet.Length, ReadUInt16(packet, 0), "PlayerStatusUpdate declared length");
        Check.Equal((ushort)0x27B6, ReadUInt16(packet, 2), "PlayerStatusUpdate opcode");
        Check.Equal(objectId, ReadUInt32(packet, 4), "PlayerStatusUpdate object id");
        Check.Equal(character.Name, ReadFixedAscii(packet, 8, 32), "PlayerStatusUpdate character name");
        Check.Equal(character.Gender, packet[40], "PlayerStatusUpdate gender");
        Check.Equal(
            (ushort)character.CurrentMap,
            ReadUInt16(packet, 42),
            "PlayerStatusUpdate current map");
        Check.Equal(character.PositionX, ReadSingle(packet, 44), "PlayerStatusUpdate X at offset 44");
        Check.Equal(0f, ReadSingle(packet, 48), "PlayerStatusUpdate terrain-height float at offset 48");
        Check.Equal(character.PositionZ, ReadSingle(packet, 52), "PlayerStatusUpdate Z at offset 52");
        Check.Equal(1f, ReadSingle(packet, 56), "PlayerStatusUpdate default locomotion multiplier at offset 56");
        Check.Equal((int)character.Profession, ReadInt32(packet, 92), "PlayerStatusUpdate profession");
        Check.Equal(
            character.Experience,
            (long)ReadUInt32(packet, 96),
            "PlayerStatusUpdate UInt32 fighter EXP");
        Check.Equal(character.Level, ReadInt32(packet, 100), "PlayerStatusUpdate level");
        Check.Equal(character.CurrentHp, ReadInt32(packet, 104), "PlayerStatusUpdate current HP");
        Check.Equal(character.CurrentMp, ReadInt32(packet, 108), "PlayerStatusUpdate current MP");
        Check.Equal(0, ReadInt32(packet, 120), "remote PlayerStatusUpdate does not disclose silver");
        Check.Equal(0, ReadInt32(packet, 124), "remote PlayerStatusUpdate does not disclose gold");
        Check.Equal(0, ReadInt32(packet, 128), "remote PlayerStatusUpdate does not disclose Honor");
        Check.Equal(character.MaxHp, ReadInt32(packet, 144), "PlayerStatusUpdate max HP");
        Check.Equal(character.MaxMp, ReadInt32(packet, 148), "PlayerStatusUpdate max MP");
        Check.Equal(PlayerRecoveryCatalog.GetTotalHp(character), ReadInt32(packet, 152), "PlayerStatusUpdate HP recovery");
        Check.Equal(PlayerRecoveryCatalog.GetTotalMp(character), ReadInt32(packet, 156), "PlayerStatusUpdate MP recovery");
        Check.Equal(character.CalculatedStats!.PhysicalAttack, ReadInt32(packet, 160), "PlayerStatusUpdate physical attack");
        Check.Equal(character.CalculatedStats.PhysicalDefense, ReadInt32(packet, 164), "PlayerStatusUpdate physical defense");
        Check.Equal(character.CalculatedStats.MagicAttack, ReadInt32(packet, 168), "PlayerStatusUpdate magic attack");
        Check.Equal(character.CalculatedStats.MagicDefense, ReadInt32(packet, 172), "PlayerStatusUpdate magic defense");
        Check.Equal(character.CalculatedStats.Hit, ReadInt32(packet, 176), "PlayerStatusUpdate hit");
        Check.Equal(character.CalculatedStats.Dodge, ReadInt32(packet, 180), "PlayerStatusUpdate dodge");
        Check.Equal(character.CalculatedStats.Critical, ReadInt32(packet, 184), "PlayerStatusUpdate critical");
        Check.Equal(character.CalculatedStats.CriticalResistance, ReadInt32(packet, 188), "PlayerStatusUpdate critical resistance");
        Check.Equal(
            character.CalculatedStats.PhysicalDamageBonus / 10000f,
            ReadSingle(packet, 192),
            "PlayerStatusUpdate physical damage bonus");
        Check.Equal(
            character.CalculatedStats.MagicDamageBonus / 10000f,
            ReadSingle(packet, 196),
            "PlayerStatusUpdate magic damage bonus");
        Check.Equal(
            character.CalculatedStats.DamageAbsorb,
            ReadInt32(packet, 200),
            "PlayerStatusUpdate damage absorb");
        Check.Equal(
            character.CalculatedStats.BeCureBonus / 10000f,
            ReadSingle(packet, 204),
            "PlayerStatusUpdate healing received");
        Check.Equal(
            character.CalculatedStats.CureBonus / 10000f,
            ReadSingle(packet, 208),
            "PlayerStatusUpdate outgoing healing");
        Check.Equal(character.TalentPoints, ReadInt32(packet, 228), "PlayerStatusUpdate talent points");

        character.Silver = 10_010_000;
        character.Gold = 73;
        character.MedusaHonorPoints = 2_025;
        var localPacket = PacketBuilder.PlayerStatusUpdate(
            character,
            movementSpeedMultiplier: 1f);
        Check.Equal(character.Silver, ReadInt32(localPacket, 120), "local PlayerStatusUpdate silver");
        Check.Equal(character.Gold, ReadInt32(localPacket, 124), "local PlayerStatusUpdate gold");
        Check.Equal(character.MedusaHonorPoints, ReadInt32(localPacket, 128), "local PlayerStatusUpdate Honor");
        var mountedPacket = PacketBuilder.PlayerStatusUpdate(character, 1.24f);
        Check.Equal(1.24f, ReadSingle(mountedPacket, 56), "local PlayerStatusUpdate mounted locomotion multiplier");

        return Task.CompletedTask;
    }
}
