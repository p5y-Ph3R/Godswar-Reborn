using System.Globalization;
using Godswar.Server.Application.Sessions;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal static class RedisGameTicketResultReader
{
    public static TicketCounts ReadCounts(RedisResult result)
    {
        var values = ReadArray(result, 2);
        return new TicketCounts(
            RedisResultReader.Integer(values[0]),
            RedisResultReader.Integer(values[1]));
    }

    public static TicketOperationResult ReadOperation(RedisResult result)
    {
        var values = ReadArray(result, 3);
        return new TicketOperationResult(
            RedisResultReader.Integer(values[0]),
            new TicketCounts(
                RedisResultReader.Integer(values[1]),
                RedisResultReader.Integer(values[2])));
    }

    public static TicketIssueScriptResult ReadIssue(RedisResult result)
    {
        var values = ReadArray(result, 4);
        return new TicketIssueScriptResult(
            RedisResultReader.Integer(values[0]),
            new TicketCounts(
                RedisResultReader.Integer(values[1]),
                RedisResultReader.Integer(values[2])),
            RedisResultReader.Integer(values[3]));
    }

    public static TicketConsumeScriptResult ReadConsume(RedisResult result)
    {
        var values = ReadArray(result, 7);
        var statusValue = RedisResultReader.Integer(values[0]);
        if (statusValue is < 1 or > 5)
        {
            throw new InvalidDataException(
                "Redis returned an invalid ticket-consume status.");
        }

        var status = (SecureTicketConsumeStatus)statusValue;
        var counts = new TicketCounts(
            RedisResultReader.Integer(values[5]),
            RedisResultReader.Integer(values[6]));
        if (status != SecureTicketConsumeStatus.Accepted)
        {
            return new TicketConsumeScriptResult(
                status,
                0,
                string.Empty,
                SecureGamePermissions.None,
                Guid.Empty,
                counts);
        }

        var accountId = ReadInt32(values[1], "account");
        var username = ReadText(values[2], "username", 32);
        SecureTicketModelValidation.ValidateAccount(accountId, username);
        var permissionsValue = ReadUInt32(values[3], "permissions");
        var permissions = (SecureGamePermissions)permissionsValue;
        if (permissions != SecureGamePermissions.EnterWorld)
        {
            throw new InvalidDataException(
                "Redis returned invalid game-ticket permissions.");
        }
        var generationText =
            ReadText(values[4], "generation", 32);
        if (!Guid.TryParseExact(
                generationText,
                "N",
                out var generationId) ||
            generationId == Guid.Empty)
        {
            throw new InvalidDataException(
                "Redis returned an invalid login-generation ID.");
        }

        return new TicketConsumeScriptResult(
            status,
            accountId,
            username,
            permissions,
            generationId,
            counts);
    }

    private static RedisResult[] ReadArray(
        RedisResult result,
        int expectedLength)
    {
        try
        {
            var values = (RedisResult[]?)result;
            if (values is null || values.Length != expectedLength)
            {
                throw new InvalidDataException(
                    "Redis returned an invalid ticket result length.");
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
                "Redis returned an invalid ticket result.",
                error);
        }
    }

    private static int ReadInt32(
        RedisResult result,
        string field)
    {
        var value = RedisResultReader.Integer(result);
        if (value is < 1 or > int.MaxValue)
        {
            throw new InvalidDataException(
                $"Redis returned an invalid ticket {field}.");
        }

        return (int)value;
    }

    private static uint ReadUInt32(
        RedisResult result,
        string field)
    {
        var value = ReadText(result, field, 10);
        if (!uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            throw new InvalidDataException(
                $"Redis returned an invalid ticket {field}.");
        }

        return parsed;
    }

    private static string ReadText(
        RedisResult result,
        string field,
        int maximumLength)
    {
        var value = result.ToString();
        if (value.Length is < 1 ||
            value.Length > maximumLength ||
            value.Any(character =>
                character is < (char)0x20 or > (char)0x7E))
        {
            throw new InvalidDataException(
                $"Redis returned an invalid ticket {field}.");
        }

        return value;
    }
}

internal readonly record struct TicketCounts(
    long ActiveGenerations,
    long OutstandingTickets);

internal readonly record struct TicketOperationResult(
    long Status,
    TicketCounts Counts);

internal readonly record struct TicketIssueScriptResult(
    long Status,
    TicketCounts Counts,
    long ExpiryUnixMilliseconds);

internal readonly record struct TicketConsumeScriptResult(
    SecureTicketConsumeStatus Status,
    int AccountId,
    string Username,
    SecureGamePermissions Permissions,
    Guid GenerationId,
    TicketCounts Counts);
