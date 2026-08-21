using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.Realms;

internal sealed class PostgresRealmCatalogReader(
    NpgsqlDataSource dataSource) : IRealmCatalogReader
{
    internal const string EnabledRealmQuery = """
        SELECT
            id,
            name,
            identifier,
            ip_address,
            game_port,
            server_limit,
            recommended,
            display_order
        FROM public.server
        WHERE enabled
        ORDER BY display_order, id
        LIMIT @rowLimit;
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public async Task<RealmCatalogSnapshot> ReadEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(
            EnabledRealmQuery);
        command.Parameters.AddWithValue(
            "rowLimit",
            RealmCatalogSnapshot.MaximumEntries + 1);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<RealmCatalogEntry>(
            RealmCatalogSnapshot.MaximumEntries);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (entries.Count == RealmCatalogSnapshot.MaximumEntries)
            {
                throw new InvalidDataException(
                    "The enabled PostgreSQL realm catalog exceeded its row bound.");
            }

            entries.Add(new RealmCatalogEntry(
                new RealmId(reader.GetInt32(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.GetInt32(7)));
        }

        return new RealmCatalogSnapshot(entries);
    }
}
