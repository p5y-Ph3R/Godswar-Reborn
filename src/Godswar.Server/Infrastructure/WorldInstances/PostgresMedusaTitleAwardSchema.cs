using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// Bounded schema add-on for disposable Medusa admission databases. Production
/// code never invokes this helper. A production migration must publish the
/// admission foundation first and then equivalent title DDL.
/// </summary>
internal static class PostgresMedusaTitleAwardSchema
{
    public static async Task CreateForDisposableDatabaseAsync(
        NpgsqlDataSource dataSource,
        string expectedDatabaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await RequireExactDisposableDatabaseAsync(
            dataSource,
            expectedDatabaseName,
            cancellationToken);
        await using var command = dataSource.CreateCommand(CreateSql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task DropForDisposableDatabaseAsync(
        NpgsqlDataSource dataSource,
        string expectedDatabaseName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await RequireExactDisposableDatabaseAsync(
            dataSource,
            expectedDatabaseName,
            cancellationToken);
        await using var command = dataSource.CreateCommand(
            """
            DROP TABLE IF EXISTS
                medusa_admission_foundation.character_title_ownership;
            DROP TABLE IF EXISTS
                medusa_admission_foundation.medusa_completion_settlements;
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RequireExactDisposableDatabaseAsync(
        NpgsqlDataSource dataSource,
        string expectedDatabaseName,
        CancellationToken cancellationToken)
    {
        if (!PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(
                expectedDatabaseName))
        {
            throw new InvalidOperationException(
                "Medusa title schema operations require a bounded disposable database.");
        }

        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        var current = await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "PostgreSQL returned no current database name.");
        if (!string.Equals(current, expectedDatabaseName, StringComparison.Ordinal) ||
            !PostgresMedusaAdmissionSchema.IsDisposableDatabaseName(current))
        {
            throw new InvalidOperationException(
                "Medusa title schema operations require the exact current database.");
        }
    }

    private const string CreateSql =
        """
        CREATE TABLE medusa_admission_foundation.medusa_completion_settlements (
            admission_id uuid PRIMARY KEY REFERENCES
                medusa_admission_foundation.admissions(admission_id),
            completion_operation_id uuid NOT NULL UNIQUE
                CHECK (completion_operation_id <>
                    '00000000-0000-0000-0000-000000000000'::uuid),
            world_instance_id uuid NOT NULL,
            difficulty smallint NOT NULL CHECK (difficulty BETWEEN 1 AND 3),
            content_map_id smallint NOT NULL CHECK (
                (difficulty = 1 AND content_map_id = 204) OR
                (difficulty IN (2, 3) AND content_map_id = 200)),
            encounter_content_fingerprint character(64) COLLATE "C" NOT NULL
                CHECK (encounter_content_fingerprint ~ '^[0-9A-F]{64}$'),
            roster_hash character(64) COLLATE "C" NOT NULL
                CHECK (roster_hash ~ '^[0-9A-F]{64}$'),
            admission_request_hash character(64) COLLATE "C" NOT NULL
                CHECK (admission_request_hash ~ '^[0-9A-F]{64}$'),
            completed_at timestamptz NOT NULL,
            elapsed_microseconds bigint NOT NULL
                CHECK (elapsed_microseconds >= 0 AND
                    elapsed_microseconds < 2400000000),
            final_score integer NOT NULL CHECK (final_score >= 3000),
            request_hash character(64) COLLATE "C" NOT NULL
                CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            title_key varchar(48) COLLATE "C" NULL,
            recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            UNIQUE (admission_id, completion_operation_id, title_key),
            CHECK (
                (difficulty = 1 AND title_key IS NULL) OR
                (difficulty = 2 AND (
                    (title_key IS NULL AND
                        elapsed_microseconds > 1200000000) OR
                    (title_key IS NOT NULL AND (
                        (title_key = 'medusa.challengers' AND
                            elapsed_microseconds <= 600000000) OR
                        (title_key = 'medusa.slayers' AND
                            elapsed_microseconds > 600000000 AND
                            elapsed_microseconds <= 900000000) OR
                        (title_key = 'medusa.executioners' AND
                            elapsed_microseconds > 900000000 AND
                            elapsed_microseconds <= 1200000000))))) OR
                (difficulty = 3 AND (
                    (title_key IS NULL AND
                        elapsed_microseconds > 1200000000) OR
                    (title_key IS NOT NULL AND (
                        (title_key = 'medusa.heir-of-perseus' AND
                            elapsed_microseconds <= 600000000) OR
                        (title_key = 'medusa.bane-of-the-three-sisters' AND
                            elapsed_microseconds > 600000000 AND
                            elapsed_microseconds <= 900000000) OR
                        (title_key = 'medusa.gorgon-breaker' AND
                            elapsed_microseconds > 900000000 AND
                            elapsed_microseconds <= 1200000000)))))
            )
        );

        CREATE TABLE medusa_admission_foundation.character_title_ownership (
            character_id integer NOT NULL CHECK (character_id > 0),
            title_key varchar(48) COLLATE "C" NOT NULL,
            source_admission_id uuid NOT NULL,
            source_completion_operation_id uuid NOT NULL,
            acquired_at timestamptz NOT NULL,
            PRIMARY KEY (character_id, title_key),
            UNIQUE (source_admission_id, character_id),
            FOREIGN KEY (
                source_admission_id,
                source_completion_operation_id,
                title_key)
            REFERENCES medusa_admission_foundation.medusa_completion_settlements(
                admission_id,
                completion_operation_id,
                title_key),
            FOREIGN KEY (source_admission_id, character_id)
            REFERENCES medusa_admission_foundation.members(
                admission_id,
                character_id)
            ON DELETE NO ACTION
        );
        """;
}
