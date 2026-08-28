using System.Data;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldInstances;

internal sealed class PostgresMedusaDailyEntryClaimStore(
    NpgsqlDataSource dataSource) : IMedusaDailyEntryClaimStore
{
    private readonly NpgsqlDataSource _dataSource = dataSource ??
        throw new ArgumentNullException(nameof(dataSource));

    public async Task<IReadOnlySet<int>> FindUsedCharacterIdsAsync(
        RealmId realmId,
        DateOnly realmDay,
        IReadOnlyCollection<int> characterIds,
        CancellationToken cancellationToken = default)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }
        ArgumentNullException.ThrowIfNull(characterIds);
        if (characterIds.Count == 0 ||
            characterIds.Any(static id => id <= 0))
        {
            throw new ArgumentException(
                "Daily-entry usage requires positive character IDs.",
                nameof(characterIds));
        }

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        var dailyEntryLimit = await ReadDailyEntryLimitAsync(
            connection,
            transaction: null,
            cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT character_id FROM medusa_daily_entries " +
            "WHERE realm_id = @realmId AND realm_day = @realmDay " +
            "AND character_id = ANY(@characterIds) " +
            "GROUP BY character_id HAVING count(*) >= @dailyEntryLimit;",
            connection);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)realmId.Value));
        command.Parameters.Add(
            "realmDay",
            NpgsqlDbType.Date).Value = realmDay;
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            characterIds.Distinct().Order().ToArray();
        command.Parameters.AddWithValue(
            "dailyEntryLimit",
            checked((short)dailyEntryLimit));

        var used = new HashSet<int>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            used.Add(reader.GetInt32(0));
        }
        return used;
    }

    public async Task<MedusaDailyEntryClaimResult> TryClaimAsync(
        MedusaDailyEntryClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var characterIds = request.CharacterIds.Order().ToArray();
        await using (var lockCommand = new NpgsqlCommand(
            LockCharactersSql,
            connection,
            transaction))
        {
            lockCommand.Parameters.Add(
                "characterIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                characterIds;
            var lockedCount = 0;
            await using var reader =
                await lockCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lockedCount++;
            }
            if (lockedCount != characterIds.Length)
            {
                throw new InvalidDataException(
                    "A Medusa daily-entry character no longer exists.");
            }
        }

        var dailyEntryLimit = await ReadDailyEntryLimitAsync(
            connection,
            transaction,
            cancellationToken);
        await using (var usageCommand = new NpgsqlCommand(
            UsageSql,
            connection,
            transaction))
        {
            usageCommand.Parameters.AddWithValue(
                "realmId",
                checked((short)request.RealmId.Value));
            usageCommand.Parameters.Add(
                "realmDay",
                NpgsqlDbType.Date).Value = request.RealmDay;
            usageCommand.Parameters.Add(
                "characterIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
                characterIds;
            usageCommand.Parameters.AddWithValue(
                "dailyEntryLimit",
                checked((short)dailyEntryLimit));
            if (await usageCommand.ExecuteScalarAsync(cancellationToken)
                is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(
                    MedusaDailyEntryClaimStatus.AlreadyUsed,
                    dailyEntryLimit);
            }
        }

        await using var command = new NpgsqlCommand(
            ClaimSql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "realmId",
            checked((short)request.RealmId.Value));
        command.Parameters.Add(
            "realmDay",
            NpgsqlDbType.Date).Value = request.RealmDay;
        command.Parameters.Add(
            "characterIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer).Value =
            characterIds;
        command.Parameters.AddWithValue(
            "reservationId",
            request.ReservationId);
        command.Parameters.AddWithValue(
            "difficulty",
            checked((short)request.Difficulty));
        command.Parameters.Add(
            "claimedAt",
            NpgsqlDbType.TimestampTz).Value =
            request.ClaimedAtUtc.UtcDateTime;

        _ = await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(MedusaDailyEntryClaimStatus.Claimed, dailyEntryLimit);
    }

    public async Task ReleaseAsync(
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        if (reservationId == Guid.Empty)
        {
            return;
        }
        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "DELETE FROM medusa_daily_entries " +
            "WHERE reservation_id = @reservationId;",
            connection);
        command.Parameters.AddWithValue("reservationId", reservationId);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<ushort> ReadDailyEntryLimitAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT daily_entry_limit FROM medusa_instance_settings " +
            "WHERE instance_key = 'medusa'" +
            (transaction is null ? ";" : " FOR SHARE;"),
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is
            short value and > 0
                ? checked((ushort)value)
                : throw new InvalidDataException(
                    "The Medusa daily-entry limit is not configured.");
    }

    private const string LockCharactersSql =
        "SELECT id FROM character_base WHERE id = ANY(@characterIds) " +
        "ORDER BY id FOR UPDATE;";

    private const string UsageSql =
        "SELECT character_id FROM medusa_daily_entries " +
        "WHERE realm_id = @realmId AND realm_day = @realmDay " +
        "AND character_id = ANY(@characterIds) " +
        "GROUP BY character_id HAVING count(*) >= @dailyEntryLimit " +
        "LIMIT 1;";

    private const string ClaimSql =
        """
        INSERT INTO medusa_daily_entries (
            realm_id, realm_day, character_id, reservation_id,
            difficulty, claimed_at)
        SELECT
            @realmId, @realmDay, character_id, @reservationId,
            @difficulty, @claimedAt
        FROM unnest(@characterIds) AS character_id;
        """;
}
