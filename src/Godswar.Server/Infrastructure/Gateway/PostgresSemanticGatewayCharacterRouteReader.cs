using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.Gateway;

internal sealed class PostgresSemanticGatewayCharacterRouteReader(
    NpgsqlDataSource dataSource) :
    ISemanticGatewayCharacterRouteReader
{
    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public async Task<SemanticGatewayCharacterRoute?>
        FindCharacterRouteAsync(
            int accountId,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        await using var command = _dataSource.CreateCommand("""
            SELECT id, "Map"
            FROM character_base
            WHERE account_id = @accountId
              AND lifecycle_state = 'active'
            ORDER BY id
            LIMIT 2;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var route = new SemanticGatewayCharacterRoute(
            reader.GetInt32(0),
            MapId.FromLegacy(checked((byte)reader.GetInt32(1))));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The account has more than one active character route.");
        }

        return route;
    }
}
