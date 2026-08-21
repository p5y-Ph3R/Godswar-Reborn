using Godswar.Server.Application.Inventory;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

/// <summary>
/// Compare-and-swap write boundary reserved for the management application.
/// Game workers deliberately do not refresh their startup-pinned snapshot.
/// </summary>
internal static class PostgresHolySpiritBalanceStore
{
    internal const string UpdateSql = """
        UPDATE public.holy_spirit_balance_settings
        SET cooled_physical_reduction_grade_one_maximum = @physicalMaximum,
            cooled_magic_reduction_grade_one_maximum = @magicMaximum,
            cooled_critical_reduction_grade_one_maximum = @criticalMaximum,
            updated_by = @updatedBy
        WHERE setting_id = 1
          AND revision = @expectedRevision
        RETURNING setting_id,
                  cooled_physical_reduction_grade_one_maximum,
                  cooled_magic_reduction_grade_one_maximum,
                  cooled_critical_reduction_grade_one_maximum,
                  revision,
                  updated_at,
                  updated_by;
        """;

    internal const string ClampSocketsSql = """
        WITH adjustable_caps(effect_id, grade_one_maximum) AS (
            VALUES
                (9, @physicalMaximum),
                (10, @magicMaximum),
                (13, @criticalMaximum)
        )
        UPDATE public.character_items
        SET holy_socket1_value = CASE
                WHEN holy_socket1_effect_id IN (9, 10, 13)
                     AND holy_socket1_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket1_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket1_level]) >
                         holy_socket1_level * (
                             SELECT grade_one_maximum
                             FROM adjustable_caps
                             WHERE effect_id = holy_socket1_effect_id)
                    THEN holy_socket1_level * (
                        SELECT grade_one_maximum
                        FROM adjustable_caps
                        WHERE effect_id = holy_socket1_effect_id)
                ELSE holy_socket1_value
            END,
            holy_socket2_value = CASE
                WHEN holy_socket2_effect_id IN (9, 10, 13)
                     AND holy_socket2_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket2_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket2_level]) >
                         holy_socket2_level * (
                             SELECT grade_one_maximum
                             FROM adjustable_caps
                             WHERE effect_id = holy_socket2_effect_id)
                    THEN holy_socket2_level * (
                        SELECT grade_one_maximum
                        FROM adjustable_caps
                        WHERE effect_id = holy_socket2_effect_id)
                ELSE holy_socket2_value
            END,
            holy_socket3_value = CASE
                WHEN holy_socket3_effect_id IN (9, 10, 13)
                     AND holy_socket3_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket3_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket3_level]) >
                         holy_socket3_level * (
                             SELECT grade_one_maximum
                             FROM adjustable_caps
                             WHERE effect_id = holy_socket3_effect_id)
                    THEN holy_socket3_level * (
                        SELECT grade_one_maximum
                        FROM adjustable_caps
                        WHERE effect_id = holy_socket3_effect_id)
                ELSE holy_socket3_value
            END,
            holy_socket4_value = CASE
                WHEN holy_socket4_effect_id IN (9, 10, 13)
                     AND holy_socket4_level BETWEEN 1 AND 10
                     AND COALESCE(
                         holy_socket4_value,
                         (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                             ::smallint[])[holy_socket4_level]) >
                         holy_socket4_level * (
                             SELECT grade_one_maximum
                             FROM adjustable_caps
                             WHERE effect_id = holy_socket4_effect_id)
                    THEN holy_socket4_level * (
                        SELECT grade_one_maximum
                        FROM adjustable_caps
                        WHERE effect_id = holy_socket4_effect_id)
                ELSE holy_socket4_value
            END
        WHERE holy_socket1_effect_id IN (9, 10, 13)
                  AND holy_socket1_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket1_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket1_level]) >
                      holy_socket1_level * (
                          SELECT grade_one_maximum
                          FROM adjustable_caps
                          WHERE effect_id = holy_socket1_effect_id)
               OR holy_socket2_effect_id IN (9, 10, 13)
                  AND holy_socket2_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket2_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket2_level]) >
                      holy_socket2_level * (
                          SELECT grade_one_maximum
                          FROM adjustable_caps
                          WHERE effect_id = holy_socket2_effect_id)
               OR holy_socket3_effect_id IN (9, 10, 13)
                  AND holy_socket3_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket3_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket3_level]) >
                      holy_socket3_level * (
                          SELECT grade_one_maximum
                          FROM adjustable_caps
                          WHERE effect_id = holy_socket3_effect_id)
               OR holy_socket4_effect_id IN (9, 10, 13)
                  AND holy_socket4_level BETWEEN 1 AND 10
                  AND COALESCE(
                      holy_socket4_value,
                      (ARRAY[80,120,170,230,300,370,500,700,950,1200]
                          ::smallint[])[holy_socket4_level]) >
                      holy_socket4_level * (
                          SELECT grade_one_maximum
                          FROM adjustable_caps
                          WHERE effect_id = holy_socket4_effect_id);
        """;

    public static async Task<HolySpiritBalanceUpdateResult> TryUpdateAsync(
        string connectionString,
        HolySpiritBalanceUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(update);
        update.Validate();
        await using var dataSource = NpgsqlDataSource.Create(
            connectionString);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        HolySpiritBalanceSnapshot? updated = null;
        await using (var command = new NpgsqlCommand(
                         UpdateSql,
                         connection,
                         transaction))
        {
            AddParameters(command, update);
            command.Parameters.AddWithValue("updatedBy", update.UpdatedBy);
            command.Parameters.AddWithValue(
                "expectedRevision",
                update.ExpectedRevision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                updated = ReadSnapshot(reader);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidDataException(
                        "The Holy Spirit balance update returned duplicate rows.");
                }
            }
        }

        if (updated is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            var current = await PostgresHolySpiritBalanceSnapshotReader
                .LoadAsync(dataSource, cancellationToken);
            return new(
                HolySpiritBalanceUpdateStatus.RevisionConflict,
                current);
        }

        updated.Validate();
        await using (var clamp = new NpgsqlCommand(
                         ClampSocketsSql,
                         connection,
                         transaction))
        {
            AddParameters(clamp, update);
            await clamp.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(
            HolySpiritBalanceUpdateStatus.Updated,
            updated);
    }

    private static void AddParameters(
        NpgsqlCommand command,
        HolySpiritBalanceUpdate update)
    {
        command.Parameters.AddWithValue(
            "physicalMaximum",
            update.CooledPhysicalReductionGradeOneMaximum);
        command.Parameters.AddWithValue(
            "magicMaximum",
            update.CooledMagicReductionGradeOneMaximum);
        command.Parameters.AddWithValue(
            "criticalMaximum",
            update.CooledCriticalReductionGradeOneMaximum);
    }

    private static HolySpiritBalanceSnapshot ReadSnapshot(
        NpgsqlDataReader reader)
    {
        if (reader.GetInt16(0) != 1)
        {
            throw new InvalidDataException(
                "The Holy Spirit balance update returned the wrong row.");
        }
        return new(
            reader.GetInt16(1),
            reader.GetInt16(2),
            reader.GetInt16(3),
            reader.GetInt64(4),
            new DateTimeOffset(reader.GetDateTime(5).ToUniversalTime()),
            reader.GetString(6));
    }
}
