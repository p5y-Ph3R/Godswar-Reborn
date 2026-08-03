using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresMakeAttributeStoneCommandExecutor
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

    private static void AddItemParameters(
        NpgsqlCommand command,
        CompactItemEntry item)
    {
        command.Parameters.AddWithValue(
            "itemId",
            checked((int)item.Id));
        AddNullableAttribute(command, "attribute1", item.Attribute1);
        AddNullableAttribute(command, "attribute2", item.Attribute2);
        AddNullableAttribute(command, "attribute3", item.Attribute3);
        AddNullableAttribute(command, "attribute4", item.Attribute4);
        AddNullableAttribute(command, "attribute5", item.Attribute5);
        AddNullableAttribute(command, "classAttribute1", item.ClassAttribute1);
        AddNullableAttribute(command, "classAttribute2", item.ClassAttribute2);
        AddNullableSmallint(
            command,
            "attributeLevel1",
            item.AttributeLevel1);
        AddNullableSmallint(
            command,
            "attributeLevel2",
            item.AttributeLevel2);
        AddNullableSmallint(
            command,
            "attributeLevel3",
            item.AttributeLevel3);
        AddNullableSmallint(
            command,
            "attributeLevel4",
            item.AttributeLevel4);
        AddNullableSmallint(
            command,
            "attributeLevel5",
            item.AttributeLevel5);
        command.Parameters.AddWithValue("itemQuality", item.Quality);
        command.Parameters.AddWithValue("itemGrade", item.Grade);
        command.Parameters.AddWithValue("bound", item.Bound);
        command.Parameters.AddWithValue("stack", item.Stack);
        command.Parameters.AddWithValue("itemExp", item.Exp);
        command.Parameters.AddWithValue(
            "holySuitCode",
            item.HolySuitCode);
        command.Parameters.AddWithValue(
            "holySocketCount",
            Math.Clamp(
                item.SocketCount,
                (short)0,
                (short)HolyStoneItemMutator.MaxSockets));
        AddNullableSmallint(
            command,
            "holySocket1EffectId",
            item.Socket1EffectId);
        AddNullableSmallint(
            command,
            "holySocket1Level",
            item.Socket1Level);
        AddNullableSmallint(
            command,
            "holySocket2EffectId",
            item.Socket2EffectId);
        AddNullableSmallint(
            command,
            "holySocket2Level",
            item.Socket2Level);
        AddNullableSmallint(
            command,
            "holySocket3EffectId",
            item.Socket3EffectId);
        AddNullableSmallint(
            command,
            "holySocket3Level",
            item.Socket3Level);
        AddNullableSmallint(
            command,
            "holySocket4EffectId",
            item.Socket4EffectId);
        AddNullableSmallint(
            command,
            "holySocket4Level",
            item.Socket4Level);
        AddNullableSmallint(
            command,
            "holySocket5EffectId",
            item.Socket5EffectId);
        AddNullableSmallint(
            command,
            "holySocket5Level",
            item.Socket5Level);
        AddNullableSmallint(
            command,
            "holySocket6EffectId",
            item.Socket6EffectId);
        AddNullableSmallint(
            command,
            "holySocket6Level",
            item.Socket6Level);
    }

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

    private static void AddNullableAttribute(
        NpgsqlCommand command,
        string name,
        int? value)
    {
        command.Parameters.Add(
            new NpgsqlParameter(name, NpgsqlDbType.Smallint)
            {
                Value = value.HasValue
                    ? checked((short)value.Value)
                    : DBNull.Value
            });
    }

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
