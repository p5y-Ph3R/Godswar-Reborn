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
            RealmId realmId,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT id, server_id, "Map"
            FROM character_base
            WHERE account_id = @accountId
              AND server_id = @realmId
              AND lifecycle_state = 'active'
            ORDER BY id
            LIMIT 2;
            """);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var route = new SemanticGatewayCharacterRoute(
            reader.GetInt32(0),
            new RealmId(reader.GetInt32(1)),
            MapId.FromLegacy(checked((byte)reader.GetInt32(2))));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The account has more than one active character route.");
        }

        return route;
    }
}
