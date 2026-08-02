using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    private static CompactItemEntry[] KitBagItems(GameCharacter character)
    {
        var kitBag = string.IsNullOrWhiteSpace(character.KitBag)
            ? GameDefaults.EmptyKitBag
            : character.KitBag;

        var slots = kitBag
            .Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(CompactItemEntry.Parse)
            .ToList();

        while (slots.Count < KitBagPageCount * KitBagSlotsPerPage)
        {
            slots.Add(default);
        }

        return slots.Take(KitBagPageCount * KitBagSlotsPerPage).ToArray();
    }

    private static void WriteEnterItemRecord(Span<byte> record, CompactItemEntry item)
    {
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], item.Id);
        WriteNullableInt32(record.Slice(4, 4), item.Attribute1);
        WriteNullableInt32(record.Slice(8, 4), item.Attribute2);
        WriteNullableInt32(record.Slice(12, 4), item.Attribute3);
        WriteNullableInt32(record.Slice(16, 4), item.Attribute4);
        WriteNullableInt32(record.Slice(20, 4), item.Attribute5);
        record[24] = ClampByte(item.Quality);
        record[25] = ClampByte(item.Grade);
        record[26] = ClampByte(item.Bound);
        record[27] = ClampByte(item.Stack);
        WriteItemExtension(record, item);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68, 4), 0x42);
    }

    private static void WriteKitBagItemRecord(Span<byte> record, CompactItemEntry item, uint ownerObjectId = LocalPlayerObjectId)
    {
        record.Clear();

        if (item.IsEmpty)
        {
            for (var offset = 0; offset <= 20; offset += 4)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record.Slice(offset, 4), -1);
            }

            record[24] = 1;
            record[25] = 1;
            record[26] = 0;
            record[27] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(28, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(32, 4), 0);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(68, 4), -1);
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], item.Id);
        WriteNullableInt32(record.Slice(4, 4), item.Attribute1);
        WriteNullableInt32(record.Slice(8, 4), item.Attribute2);
        WriteNullableInt32(record.Slice(12, 4), item.Attribute3);
        WriteNullableInt32(record.Slice(16, 4), item.Attribute4);
        WriteNullableInt32(record.Slice(20, 4), item.Attribute5);
        record[24] = ClampByte(item.Quality);
        record[25] = ClampByte(item.Grade);
        record[26] = ClampByte(item.Bound);
        record[27] = ClampByte(item.Stack);
        WriteItemExtension(record, item);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(68, 4), ownerObjectId);
    }

    private static void WriteInspectItemRecord(
        Span<byte> record,
        CompactItemEntry item,
        int characterId,
        int sourceSlot)
    {
        // Unlike the one-byte world-appearance summary, an inspect record has a
        // full byte each for quality and grade. Preserve the server values here;
        // the patched client data currently supports Q20/G25.
        WriteKitBagItemRecord(record, item);
        if (item.IsEmpty)
        {
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(64, 4), -1);
            return;
        }

        // Working-server captures keep both tail identifiers stable for a given
        // item across sessions. Reusing record-index identifiers across every
        // character lets the client cache one player's item details for another.
        // Build stable identities from the persistent character/source slot and
        // the complete item state so an upgrade also invalidates stale details.
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(64, 4),
            InspectItemStateIdentity(characterId, sourceSlot, item));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(68, 4),
            InspectItemSlotIdentity(characterId, sourceSlot));
    }

    private static uint InspectItemSlotIdentity(int characterId, int sourceSlot)
    {
        var identity = unchecked(
            0x00064000u
            + ((uint)Math.Max(characterId, 0) * 32u)
            + (uint)Math.Max(sourceSlot, 0));
        return identity is 0 or uint.MaxValue ? 0x00064001u : identity;
    }

    private static uint InspectItemStateIdentity(int characterId, int sourceSlot, CompactItemEntry item)
    {
        var hash = 2166136261u;

        AddInspectIdentityValue(ref hash, unchecked((uint)characterId));
        AddInspectIdentityValue(ref hash, unchecked((uint)sourceSlot));
        AddInspectIdentityValue(ref hash, item.Id);
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute1));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute2));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute3));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute4));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Attribute5));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel1));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel2));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel3));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel4));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.AttributeLevel5));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Quality));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Grade));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Bound));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Stack));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.Exp));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.HolySuitCode));
        AddInspectIdentityValue(ref hash, unchecked((uint)item.SocketCount));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket1EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket1Level));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket2EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket2Level));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket3EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket3Level));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket4EffectId));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket4Level));

        return hash is 0 or uint.MaxValue ? 0x3E000001u : hash;
    }

    private static uint NullableInspectIdentityValue(int? value)
    {
        return value.HasValue ? unchecked((uint)value.Value) : uint.MaxValue;
    }

    private static uint NullableInspectIdentityValue(short? value)
    {
        return value.HasValue ? unchecked((uint)value.Value) : uint.MaxValue;
    }

    private static void AddInspectIdentityValue(ref uint hash, uint value)
    {
        // Fixed FNV-1a mixing is deterministic across processes and runtimes.
        for (var shift = 0; shift < sizeof(uint) * 8; shift += 8)
        {
            hash ^= (byte)(value >> shift);
            hash *= 16777619u;
        }
    }

    private static void WriteSnapshotItemRecord(Span<byte> record, CompactItemEntry item)
    {
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[..4], item.Id);
        WriteNullableInt32(record.Slice(4, 4), item.Attribute1);
        WriteNullableInt32(record.Slice(8, 4), item.Attribute2);
        WriteNullableInt32(record.Slice(12, 4), item.Attribute3);
        WriteNullableInt32(record.Slice(16, 4), item.Attribute4);
        WriteNullableInt32(record.Slice(20, 4), item.Attribute5);
        record[24] = ClampByte(item.Quality);
        record[25] = ClampByte(item.Grade);
        record[26] = ClampByte(item.Bound);
        record[27] = ClampByte(item.Stack);
        WriteItemExtension(record, item);
    }

    private static void WriteItemExtension(Span<byte> record, CompactItemEntry item)
    {
        if (record.Length < 52 || item.IsEmpty)
        {
            return;
        }

        if (IsNativeHolyBox(item.Id))
        {
            // The stock client reads a fixed-point UInt32 at record offset 56
            // and divides it by ten for "Current accumulated EXP". A captured
            // full Holy Box V therefore carries 4,000,000,000 as 00 28 6B EE.
            // Offset 28 is equipment EXP and must remain zero for these boxes.
            if (record.Length >= 60)
            {
                var fixedPointExperience = checked((uint)Math.Clamp(
                    (long)item.Exp * 10L,
                    uint.MinValue,
                    uint.MaxValue));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    record.Slice(56, 4),
                    fixedPointExperience);
            }
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(28, 4),
                item.Exp);
        }

        // Captured item records pack holy suit and holy-stone socket count into one dword.
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(32, 2),
            (short)Math.Clamp(item.HolySuitCode, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(34, 2),
            Math.Clamp(item.SocketCount, (short)0, NativeClientHolyStoneSocketCount));

        WriteHolyStoneValueRows(record, item);
    }

    private static bool IsNativeHolyBox(uint itemId) =>
        itemId is >= 9020 and <= 9024;

    private static void WriteHolyStoneValueRows(Span<byte> record, CompactItemEntry item)
    {
        var socketCount = Math.Clamp(item.SocketCount, (short)0, NativeClientHolyStoneSocketCount);
        if (socketCount > 0)
        {
            WriteHolyStoneSlot(record, 0, item.Socket1EffectId, item.Socket1Level);
        }

        if (socketCount > 1)
        {
            WriteHolyStoneSlot(record, 1, item.Socket2EffectId, item.Socket2Level);
        }

        if (socketCount > 2)
        {
            WriteHolyStoneSlot(record, 2, item.Socket3EffectId, item.Socket3Level);
        }

        if (socketCount > 3)
        {
            WriteHolyStoneSlot(record, 3, item.Socket4EffectId, item.Socket4Level);
        }

    }

    private static void WriteHolyStoneSlot(Span<byte> record, int slot, short? effectId, short? level)
    {
        var effectOffset = 36 + (slot * 2);
        var valueOffset = 44 + (slot * 2);
        if (record.Length < Math.Max(effectOffset, valueOffset) + 2)
        {
            return;
        }

        BinaryPrimitives.WriteInt16LittleEndian(record.Slice(effectOffset, 2), HolyStoneEffectCode(effectId, level));
        BinaryPrimitives.WriteInt16LittleEndian(record.Slice(valueOffset, 2), HolyStoneValue(effectId, level));
    }

    private static short HolyStoneEffectCode(short? effectId, short? level)
    {
        if (!effectId.HasValue || !level.HasValue)
        {
            return 0;
        }

        // Captured item records store holy-stone display levels zero-based:
        // code 209 is rendered by the client as effect 2, level 10.
        var encodedLevel = Math.Clamp(level.Value, (short)1, (short)10) - 1;
        var code = (effectId.Value * 100) + encodedLevel;
        return (short)Math.Clamp(code, 0, short.MaxValue);
    }

    private static short HolyStoneValue(short? effectId, short? level)
    {
        if (!effectId.HasValue || !level.HasValue)
        {
            return 0;
        }

        var safeLevel = Math.Clamp(level.Value, (short)1, (short)10);
        var values = effectId.Value switch
        {
            // Captured working records: effect 2 L9=748, effect 2 L10=796..800.
            1 or 2 => HolyStonePercentHigh,

            // Percent-based offensive stones.
            3 or 4 => HolyStonePercentHigh,

            // Flat offensive stones.
            5 or 6 => HolyStoneFlatHigh,

            // Captured working records: effect 7 L10=596..598.
            7 => HolyStonePercentMedium,

            // Captured working records: effect 8 L9=937, effect 8 L10=991.
            8 => HolyStoneFlatCrit,

            // Captured working records: effect 9 L8=477..481, L9=506..515; effect 10 L8=463..471.
            9 or 10 or 13 or 15 or 17 or 19 => HolyStonePercentMedium,

            // Captured working records: effect 12 L8=303..311.
            11 or 12 or 14 or 16 or 18 or 20 => HolyStoneFlatLow,

            _ => HolyStonePercentMedium
        };

        return values[safeLevel - 1];
    }

    private static readonly short[] HolyStonePercentHigh =
        [110, 170, 240, 320, 410, 500, 650, 850, 1100, 1400];

    private static readonly short[] HolyStonePercentMedium =
        [80, 120, 170, 230, 300, 370, 500, 700, 950, 1200];

    private static readonly short[] HolyStoneFlatHigh =
        [120, 190, 280, 380, 500, 620, 850, 1200, 1650, 2200];

    private static readonly short[] HolyStoneFlatCrit =
        [150, 240, 340, 460, 590, 720, 950, 1300, 1800, 2400];

    private static readonly short[] HolyStoneFlatLow =
        [60, 90, 130, 170, 210, 250, 350, 500, 700, 950];

    private static void WriteNullableInt32(Span<byte> destination, int? value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, value ?? -1);
    }

    private static byte ClampByte(short value)
    {
        return (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
    }
}
