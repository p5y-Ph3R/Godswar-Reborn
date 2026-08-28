using Godswar.Server.Application.Realms;
using Npgsql;

namespace Godswar.Server.Infrastructure.Realms;

/// <summary>
/// Management-facing compare-and-swap boundary. Game workers deliberately
/// retain their startup-pinned calendar until a coordinated restart.
/// </summary>
internal sealed class PostgresRealmCalendarSettingsStore :
    IRealmCalendarSettingsStore,
    IAsyncDisposable
{
    internal const string UpdateSql = """
        WITH current_calendar AS MATERIALIZED (
            SELECT id,
                   time_zone_id,
                   time_zone_revision,
                   time_zone_updated_at,
                   time_zone_updated_by
            FROM public.server
            WHERE id = @realmId
            FOR UPDATE
        ),
        updated_calendar AS (
            UPDATE public.server realm
            SET time_zone_id = @timeZoneId,
                time_zone_revision = realm.time_zone_revision + 1,
                time_zone_updated_at = clock_timestamp(),
                time_zone_updated_by = @updatedBy
            FROM current_calendar current
            WHERE realm.id = current.id
              AND current.time_zone_revision = @expectedRevision
              AND current.time_zone_id IS DISTINCT FROM @timeZoneId
            RETURNING realm.id,
                      realm.time_zone_id,
                      realm.time_zone_revision,
                      realm.time_zone_updated_at,
                      realm.time_zone_updated_by
        )
        SELECT 1::smallint AS outcome,
               updated.id,
               updated.time_zone_id,
               updated.time_zone_revision,
               updated.time_zone_updated_at,
               updated.time_zone_updated_by
        FROM updated_calendar updated
        UNION ALL
        SELECT CASE
                   WHEN current.time_zone_revision <> @expectedRevision
                       THEN 3::smallint
                   ELSE 2::smallint
               END AS outcome,
               current.id,
               current.time_zone_id,
               current.time_zone_revision,
               current.time_zone_updated_at,
               current.time_zone_updated_by
        FROM current_calendar current
        WHERE NOT EXISTS (SELECT 1 FROM updated_calendar);
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _ownsDataSource;

    public PostgresRealmCalendarSettingsStore(NpgsqlDataSource dataSource) :
        this(dataSource, ownsDataSource: false)
    {
    }

    private PostgresRealmCalendarSettingsStore(
        NpgsqlDataSource dataSource,
        bool ownsDataSource)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownsDataSource = ownsDataSource;
    }

    public static PostgresRealmCalendarSettingsStore Create(
        string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new(
            NpgsqlDataSource.Create(connectionString),
            ownsDataSource: true);
    }

    public async Task<RealmCalendarUpdateResult> TryUpdateAsync(
        RealmCalendarUpdate update,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        return await TryUpdateAsync(
            update,
            connection,
            transaction: null,
            cancellationToken);
    }

    internal static async Task<RealmCalendarUpdateResult> TryUpdateAsync(
        RealmCalendarUpdate update,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(connection);
        update.Validate();
        await using var command = new NpgsqlCommand(
            UpdateSql,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", update.RealmId.Value);
        command.Parameters.AddWithValue("timeZoneId", update.TimeZoneId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            update.ExpectedRevision);
        command.Parameters.AddWithValue("updatedBy", update.UpdatedBy);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(
                RealmCalendarUpdateStatus.RealmMissing,
                Calendar: null);
        }

        var status = reader.GetInt16(0) switch
        {
            1 => RealmCalendarUpdateStatus.Updated,
            2 => RealmCalendarUpdateStatus.Unchanged,
            3 => RealmCalendarUpdateStatus.RevisionConflict,
            var value => throw new InvalidDataException(
                $"Unknown realm calendar update outcome {value}.")
        };
        var calendar = new RealmCalendar(
            new(reader.GetInt32(1)),
            reader.GetString(2),
            reader.GetInt64(3),
            new DateTimeOffset(reader.GetDateTime(4).ToUniversalTime()),
            reader.GetString(5));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "Realm calendar CAS returned duplicate rows.");
        }
        return new(status, calendar);
    }

    public ValueTask DisposeAsync() =>
        _ownsDataSource
            ? _dataSource.DisposeAsync()
            : ValueTask.CompletedTask;
}
