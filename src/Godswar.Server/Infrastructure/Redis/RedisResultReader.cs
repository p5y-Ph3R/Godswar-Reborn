using System.Globalization;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal static class RedisResultReader
{
    public static long Integer(RedisResult result)
    {
        try
        {
            return (long)result;
        }
        catch (Exception error)
        {
            throw new InvalidDataException(
                "Redis returned an invalid integer result.",
                error);
        }
    }

    public static (long Status, long Value) Pair(RedisResult result)
    {
        try
        {
            var values = (RedisResult[]?)result;
            if (values is null || values.Length != 2)
            {
                throw new InvalidDataException(
                    "Redis returned an invalid pair length.");
            }

            return (Integer(values[0]), Integer(values[1]));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidDataException(
                "Redis returned an invalid pair result.",
                error);
        }
    }

    public static (long Status, long Value, long Timestamp) Triple(
        RedisResult result)
    {
        try
        {
            var values = (RedisResult[]?)result;
            if (values is null || values.Length != 3)
            {
                throw new InvalidDataException(
                    "Redis returned an invalid triple length.");
            }

            return (
                Integer(values[0]),
                Integer(values[1]),
                Integer(values[2]));
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidDataException(
                "Redis returned an invalid triple result.",
                error);
        }
    }
}

internal sealed class RedisHashReader
{
    private const int MaximumFields = 32;
    private readonly Dictionary<string, RedisValue> _values;

    public RedisHashReader(HashEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length is < 1 or > MaximumFields)
        {
            throw new InvalidDataException(
                "Redis coordination hash field count is invalid.");
        }

        _values = new Dictionary<string, RedisValue>(
            entries.Length,
            StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var name = entry.Name.ToString();
            if (name.Length is < 1 or > 32 ||
                !_values.TryAdd(name, entry.Value))
            {
                throw new InvalidDataException(
                    "Redis coordination hash contains an invalid field.");
            }
        }
    }

    public string RequiredString(string name, int maximumLength)
    {
        if (!_values.TryGetValue(name, out var value) ||
            value.IsNull)
        {
            throw new InvalidDataException(
                $"Redis coordination field '{name}' is missing.");
        }
        var text = value.ToString();
        if (text.Length is < 1 ||
            text.Length > maximumLength ||
            text.Any(character =>
                character is < (char)0x20 or > (char)0x7E))
        {
            throw new InvalidDataException(
                $"Redis coordination field '{name}' is invalid.");
        }

        return text;
    }

    public Guid RequiredGuid(string name)
    {
        var value = RequiredString(name, 32);
        if (!Guid.TryParseExact(value, "N", out var parsed) ||
            parsed == Guid.Empty)
        {
            throw new InvalidDataException(
                $"Redis coordination field '{name}' is not a valid ID.");
        }

        return parsed;
    }

    public byte RequiredByte(string name)
    {
        var value = RequiredInt64(name);
        return checked((byte)value);
    }

    public short RequiredInt16(string name)
    {
        var value = RequiredInt64(name);
        return checked((short)value);
    }

    public int RequiredInt32(string name)
    {
        var value = RequiredInt64(name);
        return checked((int)value);
    }

    public long RequiredInt64(string name)
    {
        var value = RequiredString(name, 32);
        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException(
                $"Redis coordination field '{name}' is not an integer.");
        }

        return parsed;
    }

    public DateTimeOffset RequiredDateTimeOffset(string name)
    {
        var milliseconds = RequiredInt64(name);
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException(
                $"Redis coordination field '{name}' is not a timestamp.",
                error);
        }
    }
}
