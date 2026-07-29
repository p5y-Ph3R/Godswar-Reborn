using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolyStoneCommandExecutor
{
    private static CompactItemEntry ReadCompactItem(
        NpgsqlDataReader reader) =>
        new(
            checked((uint)reader.GetInt32(3)),
            ReadNullableAttribute(reader, 4),
            ReadNullableAttribute(reader, 5),
            ReadNullableAttribute(reader, 6),
            ReadNullableAttribute(reader, 7),
            ReadNullableAttribute(reader, 8),
            reader.GetInt16(14),
            reader.GetInt16(15),
            reader.GetInt16(16),
            reader.GetInt16(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            ReadNullableSmallint(reader, 9),
            ReadNullableSmallint(reader, 10),
            ReadNullableSmallint(reader, 11),
            ReadNullableSmallint(reader, 12),
            ReadNullableSmallint(reader, 13),
            reader.GetInt16(20),
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
            ReadNullableSmallint(reader, 31),
            ReadNullableSmallint(reader, 32));

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

    private static void AddNullableSmallint(
        NpgsqlCommand command,
        string name,
        short? value)
    {
        command.Parameters.Add(
            new NpgsqlParameter(name, NpgsqlDbType.Smallint)
            {
                Value = value.HasValue
                    ? value.Value
                    : DBNull.Value
            });
    }
}
