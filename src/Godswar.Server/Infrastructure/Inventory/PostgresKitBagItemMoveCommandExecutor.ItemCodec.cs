using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemMoveCommandExecutor
{
    private static CompactItemEntry ReadCompactItem(
        NpgsqlDataReader reader) =>
        new(
            checked((uint)reader.GetInt32(2)),
            ReadNullableAttribute(reader, 3),
            ReadNullableAttribute(reader, 4),
            ReadNullableAttribute(reader, 5),
            ReadNullableAttribute(reader, 6),
            ReadNullableAttribute(reader, 7),
            reader.GetInt16(13),
            reader.GetInt16(14),
            reader.GetInt16(15),
            reader.GetInt16(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            ReadNullableSmallint(reader, 8),
            ReadNullableSmallint(reader, 9),
            ReadNullableSmallint(reader, 10),
            ReadNullableSmallint(reader, 11),
            ReadNullableSmallint(reader, 12),
            reader.GetInt16(19),
            ReadNullableSmallint(reader, 20),
            ReadNullableSmallint(reader, 21),
            ReadNullableSmallint(reader, 22),
            ReadNullableSmallint(reader, 23),
            ReadNullableSmallint(reader, 24),
            ReadNullableSmallint(reader, 25),
            ReadNullableSmallint(reader, 26),
            ReadNullableSmallint(reader, 27),
            ReadNullableSmallint(reader, 28),
            ReadNullableSmallint(reader, 29),
            ReadNullableSmallint(reader, 30),
            ReadNullableSmallint(reader, 31))
        {
            ClassAttribute1 = ReadNullableAttribute(reader, 33),
            ClassAttribute2 = ReadNullableAttribute(reader, 34)
        };

    private static int? ReadNullableAttribute(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt16(ordinal);

    private static short? ReadNullableSmallint(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetInt16(ordinal);
}
