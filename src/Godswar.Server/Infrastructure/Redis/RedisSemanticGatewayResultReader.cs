using System.Globalization;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal static class RedisSemanticGatewayResultReader
{
    public static RedisResult[] Array(
        RedisResult result,
        int expectedLength)
    {
        try
        {
            var values = (RedisResult[]?)result;
            if (values is null || values.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "Redis returned an invalid semantic-gateway result.");
            }

            return values;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidDataException(
                "Redis returned a malformed semantic-gateway result.",
                error);
        }
    }

    public static long Int64(RedisResult result)
    {
        var value = Text(result, 32, allowEmpty: false);
        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException(
                "Redis returned a non-integer semantic-gateway field.");
        }

        return parsed;
    }

    public static int Int32(RedisResult result) =>
        checked((int)Int64(result));

    public static string Text(
        RedisResult result,
        int maximumLength,
        bool allowEmpty = false)
    {
        var value = result.ToString();
        if ((!allowEmpty && value.Length == 0) ||
            value.Length > maximumLength ||
            value.Any(character =>
                character is < (char)0x20 or > (char)0x7E))
        {
            throw new InvalidDataException(
                "Redis returned an invalid bounded text field.");
        }

        return value;
    }

    public static Guid Guid(RedisResult result)
    {
        var value = Text(result, 32);
        if (!System.Guid.TryParseExact(
                value,
                "N",
                out var parsed) ||
            parsed == System.Guid.Empty)
        {
            throw new InvalidDataException(
                "Redis returned an invalid semantic-gateway ID.");
        }

        return parsed;
    }

    public static DateTimeOffset Timestamp(RedisResult result)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(
                Int64(result));
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException(
                "Redis returned an invalid semantic-gateway timestamp.",
                error);
        }
    }
}
