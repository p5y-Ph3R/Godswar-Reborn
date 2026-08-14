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
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.ClassAttribute1));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.ClassAttribute2));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.ElementalAttribute1));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.ElementalAttribute2));
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
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket1Value));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket2Value));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket3Value));
        AddInspectIdentityValue(ref hash, NullableInspectIdentityValue(item.Socket4Value));

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

        if (record.Length >= 60 && IsNativePreStone(item.Id))
        {
            // The stock PreStone tooltip reads the dword at record offset 56
            // and renders (value % 100) + 1 as its level. Grade is the durable
            // server level, so encode its zero-based native representation.
            var encodedLevel = Math.Clamp(
                item.Grade,
                (short)HolyStoneUpgradePolicy.MinimumLevel,
                (short)HolyStoneUpgradePolicy.MaximumLevel) - 1;
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(56, 4),
                encodedLevel);
        }

        if (record.Length >= 60 && item.Id == PetItemCatalog.PackedSealJade)
        {
            if (item.LinkedSealedPetId is <= 0 or > uint.MaxValue)
            {
                throw new InvalidDataException(
                    "A packed Seal Jade requires a native-width linked pet ID.");
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                record.Slice(56, 4),
                checked((uint)item.LinkedSealedPetId));
        }

        // Captured item records pack holy suit and holy-stone socket count into one dword.
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(32, 2),
            (short)Math.Clamp(item.HolySuitCode, short.MinValue, short.MaxValue));
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(34, 2),
            Math.Clamp(item.SocketCount, (short)0, NativeClientHolyStoneSocketCount));

        WriteHolyStoneValueRows(record, item);
        WriteClassSuitAttributeExtension(record, item);
    }

    private static void WriteClassSuitAttributeExtension(
        Span<byte> record,
        CompactItemEntry item)
    {
        // Offsets 52..63 are reserved in retained native equipment captures.
        // Holy Boxes already use offset 56 for stored EXP and can never carry
        // Class Suit attributes, so the marker prevents cross-item ambiguity.
        if (record.Length < 64 ||
            !HasCanonicalClassSuitAttributeExtension(item))
        {
            return;
        }

        WriteNullableInt32(record.Slice(52, 4), item.ClassAttribute1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(56, 4),
            PackElementalAttributes(item));
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.Slice(60, 4),
            ClassSuitAttributeExtensionMarker);
    }

    private static bool HasCanonicalClassSuitAttributeExtension(
        CompactItemEntry item)
    {
        if (!ClassSuitConversionCatalog.TryResolveSuit(
                item.Id,
                out _,
                out var tier) ||
            tier is not (ClassSuitTier.TierIII or ClassSuitTier.TierIV) ||
            item.Grade is < 1 or > 25 ||
            (!item.ClassAttribute1.HasValue &&
             !item.ElementalAttribute1.HasValue) ||
            !ElementalAttributeCatalog.HasCanonicalDedicatedAttributeShape(
                item))
        {
            return false;
        }

        return true;
    }

    private static uint PackElementalAttributes(CompactItemEntry item)
    {
        const ushort empty = ushort.MaxValue;
        var first = item.ElementalAttribute1.HasValue
            ? checked((ushort)item.ElementalAttribute1.Value)
            : empty;
        var second = item.ElementalAttribute2.HasValue
            ? checked((ushort)item.ElementalAttribute2.Value)
            : empty;
        return first | ((uint)second << 16);
    }

    private static bool IsNativeHolyBox(uint itemId) =>
        itemId is >= 9020 and <= 9024;

    private static bool IsNativePreStone(uint itemId) =>
        HolyStoneUpgradePolicy.IsHolyStone(itemId);

    private static void WriteHolyStoneValueRows(Span<byte> record, CompactItemEntry item)
    {
        var socketCount = Math.Clamp(item.SocketCount, (short)0, NativeClientHolyStoneSocketCount);
        if (socketCount > 0)
        {
            WriteHolyStoneSlot(
                record,
                0,
                item.Socket1EffectId,
                item.Socket1Level,
                item.Socket1Value);
        }

        if (socketCount > 1)
        {
            WriteHolyStoneSlot(
                record,
                1,
                item.Socket2EffectId,
                item.Socket2Level,
                item.Socket2Value);
        }

        if (socketCount > 2)
        {
            WriteHolyStoneSlot(
                record,
                2,
                item.Socket3EffectId,
                item.Socket3Level,
                item.Socket3Value);
        }

        if (socketCount > 3)
        {
            WriteHolyStoneSlot(
                record,
                3,
                item.Socket4EffectId,
                item.Socket4Level,
                item.Socket4Value);
        }

    }

    private static void WriteHolyStoneSlot(
        Span<byte> record,
        int slot,
        short? effectId,
        short? level,
        short? effectivenessValue)
    {
        var effectOffset = 36 + (slot * 2);
        var valueOffset = 44 + (slot * 2);
        if (record.Length < Math.Max(effectOffset, valueOffset) + 2)
        {
            return;
        }

        BinaryPrimitives.WriteInt16LittleEndian(record.Slice(effectOffset, 2), HolyStoneEffectCode(effectId, level));
        BinaryPrimitives.WriteInt16LittleEndian(
            record.Slice(valueOffset, 2),
            HolyStoneValue(effectId, level, effectivenessValue));
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

    private static short HolyStoneValue(
        short? effectId,
        short? level,
        short? effectivenessValue)
    {
        if (!effectId.HasValue || !level.HasValue)
        {
            return 0;
        }

        if (effectivenessValue.HasValue)
        {
            return effectivenessValue.Value;
        }
        return HolySpiritLegacyEffectiveness.TryResolve(
                effectId.Value,
                level.Value,
                out var legacyValue)
            ? legacyValue
            : (short)0;
    }

    private static void WriteNullableInt32(Span<byte> destination, int? value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, value ?? -1);
    }

    private static byte ClampByte(short value)
    {
        return (byte)Math.Clamp(value, byte.MinValue, byte.MaxValue);
    }

}
