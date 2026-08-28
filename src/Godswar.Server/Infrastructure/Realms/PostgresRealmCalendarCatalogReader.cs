using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Npgsql;

namespace Godswar.Server.Infrastructure.Realms;

internal sealed class PostgresRealmCalendarCatalogReader(
    NpgsqlDataSource dataSource) : IRealmCalendarCatalogReader
{
    internal const string ReadSql = """
        SELECT id,
               time_zone_id,
               time_zone_revision,
               time_zone_updated_at,
               time_zone_updated_by
        FROM public.server
        ORDER BY id
        LIMIT @rowLimit;
        """;

    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public static async Task<RealmCalendarCatalog> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        return await new PostgresRealmCalendarCatalogReader(dataSource)
            .ReadAsync(cancellationToken);
    }

    public async Task<RealmCalendarCatalog> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(
            connection,
            transaction: null,
            cancellationToken);
    }

    internal static async Task<RealmCalendarCatalog> ReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var command = new NpgsqlCommand(
            ReadSql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "rowLimit",
            RealmCalendarCatalog.MaximumEntries + 1);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<RealmCalendar>(
            RealmCalendarCatalog.MaximumEntries);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (entries.Count == RealmCalendarCatalog.MaximumEntries)
            {
                throw new InvalidDataException(
                    "The persisted realm calendar catalog exceeded its bound.");
            }
            entries.Add(ReadCalendar(reader));
        }
        return new RealmCalendarCatalog(entries);
    }

    internal static RealmCalendar ReadCalendar(NpgsqlDataReader reader) =>
        new(
            new RealmId(reader.GetInt32(0)),
            reader.GetString(1),
            reader.GetInt64(2),
            new DateTimeOffset(reader.GetDateTime(3).ToUniversalTime()),
            reader.GetString(4));
}
