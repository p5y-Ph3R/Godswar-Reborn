using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private static byte ToWorldProfessionByte(byte profession)
    {
        // Working-server captures show the world-spawn class byte matches the DB class id.
        return profession;
    }

    private static byte ToClientProfessionByte(byte profession)
    {
        // UI/detail packets use the DB/client gameplay class id. World-spawn visuals use a different avatar order.
        return profession;
    }

    private static string EquipmentFor(GameCharacter character)
    {
        return string.IsNullOrWhiteSpace(character.Equipment)
            ? GameDefaults.DefaultEquipment(character.Profession)
            : character.Equipment;
    }

    private static ReadOnlySpan<byte> ValidPacketStreamPrefix(ReadOnlySpan<byte> stream)
    {
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            offset += length;
        }

        return stream[..offset];
    }

    private static byte[] PatchReferencePlayerPackets(ReadOnlySpan<byte> stream, GameCharacter character)
    {
        using var output = new MemoryStream(stream.Length);
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            var packet = stream.Slice(offset, length);
            if (Contains(packet, ReferencePlayerName))
            {
                var patched = PlayerDetail(character);
                output.Write(patched);
            }
            else
            {
                output.Write(packet);
            }

            offset += length;
        }

        return output.ToArray();
    }

    private static void PatchReferencePlayerPacket(byte[] packet, GameCharacter character, int nameOffset)
    {
        if (packet.Length < nameOffset + 32)
        {
            return;
        }

        PacketText.WriteFixedAscii(packet.AsSpan(nameOffset, 32), character.Name);

        var fieldBase = nameOffset + 32;
        var calculatedStats = character.CalculatedStats is null
            ? null
            : CharacterStats.FromCharacter(character);
        if (packet.Length > fieldBase)
        {
            packet[fieldBase] = character.Gender;
        }

        if (packet.Length >= fieldBase + 20)
        {
            // PlayerDetail and PlayerStatusUpdate share the captured transform
            // layout. Do not leak the fixed template player's 165/-97 position
            // when publishing an object-specific remote status packet.
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 4, 4), character.PositionX);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 8, 4), 0f);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 12, 4), character.PositionZ);
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 16, 4), 1f);
        }

        if (packet.Length >= fieldBase + 56)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 52, 4), ToClientProfessionByte(character.Profession));
        }

        if (packet.Length >= fieldBase + 60)
        {
            WriteLegacyFighterExperience(
                packet.AsSpan(fieldBase + 56, 4),
                character.Experience,
                nameof(character.Experience));
        }

        if (packet.Length >= fieldBase + 64)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 60, 4), character.Level);
        }

        if (packet.Length >= fieldBase + 72)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 64, 4), character.CurrentHp);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 68, 4), character.CurrentMp);
        }

        if (packet.Length >= fieldBase + 112)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 104, 4), character.MaxHp);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 108, 4), character.MaxMp);
        }

        if (packet.Length >= fieldBase + 152 &&
            calculatedStats is { } stats)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(fieldBase + 112, 4),
                PlayerRecoveryCatalog.GetTotalHp(character));
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(fieldBase + 116, 4),
                PlayerRecoveryCatalog.GetTotalMp(character));
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 120, 4), stats.PhysicalAttack);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 124, 4), stats.PhysicalDefense);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 128, 4), stats.MagicAttack);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 132, 4), stats.MagicDefense);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 136, 4), stats.Hit);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 140, 4), stats.Dodge);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 144, 4), stats.Critical);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 148, 4), stats.CriticalResistance);
        }

        if (packet.Length >= fieldBase + 160 &&
            calculatedStats is { } extendedStats)
        {
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 152, 4), ToClientPercent(extendedStats.PhysicalDamageBonus));
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 156, 4), ToClientPercent(extendedStats.MagicDamageBonus));
        }

        if (packet.Length >= fieldBase + 164 &&
            calculatedStats is { } defensePierceStats)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(fieldBase + 160, 4), defensePierceStats.DamageAbsorb);
        }

        if (packet.Length >= fieldBase + 172 &&
            calculatedStats is { } healingStats)
        {
            // The legacy GameData fields immediately following DamageAbsorb
            // are healing received (AcceptCure) and outgoing healing (Cure).
            // PersonalInfoUI reads its Healing value from the latter field.
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 164, 4), ToClientPercent(healingStats.BeCureBonus));
            BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(fieldBase + 168, 4), ToClientPercent(healingStats.CureBonus));
        }
    }

    private static float ToClientPercent(int scaledPercent)
    {
        return scaledPercent / 10000f;
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0)
        {
            return true;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static void ReplaceAscii(Span<byte> packet, ReadOnlySpan<byte> search, ReadOnlySpan<byte> replacement)
    {
        if (search.Length == 0 || search.Length != replacement.Length)
        {
            return;
        }

        for (var i = 0; i <= packet.Length - search.Length; i++)
        {
            if (!packet.Slice(i, search.Length).SequenceEqual(search))
            {
                continue;
            }

            replacement.CopyTo(packet.Slice(i, replacement.Length));
            i += search.Length - 1;
        }
    }
}
