using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal static class WarehouseItemStateCodec
{
    internal const string SelectCompactColumns =
        """
        prop_id,
        attribute1, attribute2, attribute3, attribute4, attribute5,
        attribute_level1, attribute_level2, attribute_level3,
        attribute_level4, attribute_level5,
        item_quality, item_grade, bound, stack, item_exp,
        holy_suit_code, holy_socket_count,
        holy_socket1_effect_id, holy_socket1_level,
        holy_socket2_effect_id, holy_socket2_level,
        holy_socket3_effect_id, holy_socket3_level,
        holy_socket4_effect_id, holy_socket4_level,
        holy_socket5_effect_id, holy_socket5_level,
        holy_socket6_effect_id, holy_socket6_level,
        class_attribute1, class_attribute2,
        elemental_attribute1, elemental_attribute2,
        holy_socket1_value, holy_socket2_value,
        holy_socket3_value, holy_socket4_value
        """;

    public static CompactItemEntry ReadCompactItem(
        NpgsqlDataReader reader,
        int start = 0) =>
        new(
            checked((uint)reader.GetInt32(start)),
            ReadNullableAttribute(reader, start + 1),
            ReadNullableAttribute(reader, start + 2),
            ReadNullableAttribute(reader, start + 3),
            ReadNullableAttribute(reader, start + 4),
            ReadNullableAttribute(reader, start + 5),
            reader.GetInt16(start + 11),
            reader.GetInt16(start + 12),
            reader.GetInt16(start + 13),
            reader.GetInt16(start + 14),
            reader.GetInt32(start + 15),
            reader.GetInt32(start + 16),
            ReadNullableSmallint(reader, start + 6),
            ReadNullableSmallint(reader, start + 7),
            ReadNullableSmallint(reader, start + 8),
            ReadNullableSmallint(reader, start + 9),
            ReadNullableSmallint(reader, start + 10),
            reader.GetInt16(start + 17),
            ReadNullableSmallint(reader, start + 18),
            ReadNullableSmallint(reader, start + 19),
            ReadNullableSmallint(reader, start + 20),
            ReadNullableSmallint(reader, start + 21),
            ReadNullableSmallint(reader, start + 22),
            ReadNullableSmallint(reader, start + 23),
            ReadNullableSmallint(reader, start + 24),
            ReadNullableSmallint(reader, start + 25),
            ReadNullableSmallint(reader, start + 26),
            ReadNullableSmallint(reader, start + 27),
            ReadNullableSmallint(reader, start + 28),
            ReadNullableSmallint(reader, start + 29))
        {
            ClassAttribute1 = ReadNullableAttribute(reader, start + 30),
            ClassAttribute2 = ReadNullableAttribute(reader, start + 31),
            ElementalAttribute1 = ReadNullableAttribute(reader, start + 32),
            ElementalAttribute2 = ReadNullableAttribute(reader, start + 33),
            Socket1Value = ReadNullableSmallint(reader, start + 34),
            Socket2Value = ReadNullableSmallint(reader, start + 35),
            Socket3Value = ReadNullableSmallint(reader, start + 36),
            Socket4Value = ReadNullableSmallint(reader, start + 37)
        };

    private static int? ReadNullableAttribute(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);

    private static short? ReadNullableSmallint(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt16(ordinal);
}
