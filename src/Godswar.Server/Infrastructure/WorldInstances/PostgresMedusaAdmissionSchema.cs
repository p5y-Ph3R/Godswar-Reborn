using System.Text.RegularExpressions;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldInstances;

/// <summary>
/// Isolated pre-migration schema bootstrap for disposable integration databases
/// only. The production store never invokes it. Normal rollout must add the
/// same DDL through the repository's migration authority in a later slice.
/// </summary>
internal static class PostgresMedusaAdmissionSchema
{
    internal const string SchemaName = "medusa_admission_foundation";

    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|medusa_[a-z0-9]{8,40})$",
        RegexOptions.CultureInvariant);

    internal static bool IsDisposableDatabaseName(string databaseName) =>
        !string.IsNullOrWhiteSpace(databaseName) &&
        DisposableDatabasePattern.IsMatch(databaseName);

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
            $"DROP SCHEMA IF EXISTS {SchemaName} CASCADE;");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RequireExactDisposableDatabaseAsync(
        NpgsqlDataSource dataSource,
        string expectedDatabaseName,
        CancellationToken cancellationToken)
    {
        if (!IsDisposableDatabaseName(expectedDatabaseName))
        {
            throw new InvalidOperationException(
                "Medusa admission schema operations require an explicitly " +
                "named bounded disposable database.");
        }

        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        var current = await command.ExecuteScalarAsync(cancellationToken)
            as string ?? throw new InvalidDataException(
                "PostgreSQL returned no current database name.");
        if (!string.Equals(
                current,
                expectedDatabaseName,
                StringComparison.Ordinal) ||
            !IsDisposableDatabaseName(current))
        {
            throw new InvalidOperationException(
                "Medusa admission schema operations require the current " +
                "database to exactly match the approved disposable name.");
        }
    }

    private const string CreateSql =
        """
        CREATE SCHEMA medusa_admission_foundation;

        CREATE TABLE medusa_admission_foundation.admissions (
            admission_id uuid PRIMARY KEY,
            world_instance_id uuid NOT NULL UNIQUE,
            realm_id integer NOT NULL CHECK (realm_id > 0),
            realm_day date NOT NULL,
            calendar_time_zone_id varchar(64) COLLATE "C" NOT NULL,
            time_zone_rules_fingerprint character(64) COLLATE "C" NOT NULL
                CHECK (time_zone_rules_fingerprint ~ '^[0-9A-F]{64}$'),
            calendar_revision bigint NOT NULL CHECK (calendar_revision > 0),
            difficulty smallint NOT NULL CHECK (difficulty BETWEEN 1 AND 3),
            content_map_id smallint NOT NULL CHECK (content_map_id >= 0),
            source_world_instance_id uuid NOT NULL,
            source_map_id smallint NOT NULL CHECK (source_map_id >= 0),
            source_npc_id bigint NOT NULL CHECK (source_npc_id > 0),
            lease_id uuid NOT NULL,
            party_id uuid NOT NULL,
            party_revision bigint NOT NULL CHECK (party_revision > 0),
            leader_account_id integer NOT NULL CHECK (leader_account_id > 0),
            leader_character_id integer NOT NULL CHECK (leader_character_id > 0),
            lease_issued_at timestamptz NOT NULL,
            lease_expires_at timestamptz NOT NULL,
            member_count smallint NOT NULL CHECK (member_count BETWEEN 1 AND 5),
            roster_hash character(64) COLLATE "C" NOT NULL
                CHECK (roster_hash ~ '^[0-9A-F]{64}$'),
            request_hash character(64) COLLATE "C" NOT NULL
                CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            encounter_content_fingerprint character(64) COLLATE "C" NOT NULL
                CHECK (encounter_content_fingerprint ~ '^[0-9A-F]{64}$'),
            state smallint NOT NULL CHECK (state BETWEEN 1 AND 12),
            revision bigint NOT NULL CHECK (revision > 0),
            roster_transfer_stage_id uuid NULL,
            roster_transfer_preparation_hash character(64) COLLATE "C" NULL
                CHECK (
                    roster_transfer_preparation_hash IS NULL OR
                    roster_transfer_preparation_hash ~ '^[0-9A-F]{64}$'),
            reserved_at timestamptz NOT NULL,
            runtime_ready_at timestamptz NULL,
            roster_transfer_committed_at timestamptz NULL,
            consumed_at timestamptz NULL,
            terminal_at timestamptz NULL,
            released_at timestamptz NULL,
            cleanup_kind smallint NULL CHECK (cleanup_kind IN (1, 2)),
            cleanup_roster_operation_id uuid NULL,
            cleanup_runtime_operation_id uuid NULL,
            cleanup_completed_at timestamptz NULL,
            CHECK (world_instance_id <> source_world_instance_id),
            CHECK (lease_expires_at > lease_issued_at),
            CHECK (
                (state IN (1, 2, 8, 12) AND
                    roster_transfer_stage_id IS NULL AND
                    roster_transfer_preparation_hash IS NULL) OR
                (state IN (3, 4, 5, 6, 7, 9, 10, 11) AND
                    roster_transfer_stage_id IS NOT NULL AND
                    roster_transfer_preparation_hash IS NOT NULL)
            ),
            CHECK (
                (state BETWEEN 1 AND 8 AND
                    cleanup_kind IS NULL AND
                    cleanup_roster_operation_id IS NULL AND
                    cleanup_runtime_operation_id IS NULL AND
                    cleanup_completed_at IS NULL) OR
                (state IN (9, 10, 11) AND cleanup_kind = 2 AND
                    cleanup_roster_operation_id IS NOT NULL AND
                    cleanup_runtime_operation_id IS NOT NULL AND
                    cleanup_completed_at IS NOT NULL) OR
                (state = 12 AND cleanup_kind = 1 AND
                    cleanup_roster_operation_id IS NOT NULL AND
                    cleanup_runtime_operation_id IS NOT NULL AND
                    cleanup_completed_at IS NOT NULL)
            ),
            CHECK (reserved_at >= lease_issued_at AND reserved_at < lease_expires_at),
            CHECK (runtime_ready_at IS NULL OR runtime_ready_at >= reserved_at),
            CHECK (
                roster_transfer_committed_at IS NULL OR
                (runtime_ready_at IS NOT NULL AND
                 roster_transfer_committed_at >= runtime_ready_at)),
            CHECK (
                consumed_at IS NULL OR
                (roster_transfer_committed_at IS NOT NULL AND
                 consumed_at >= roster_transfer_committed_at)),
            CHECK (
                terminal_at IS NULL OR
                (consumed_at IS NOT NULL AND terminal_at >= consumed_at)),
            CHECK (
                released_at IS NULL OR
                released_at >= COALESCE(
                    roster_transfer_committed_at,
                    runtime_ready_at,
                    reserved_at)),
            CHECK (
                cleanup_completed_at IS NULL OR
                cleanup_completed_at >= COALESCE(
                    released_at,
                    terminal_at,
                    reserved_at)),
            CHECK (
                (state = 1 AND runtime_ready_at IS NULL AND
                    roster_transfer_committed_at IS NULL AND consumed_at IS NULL AND
                    terminal_at IS NULL AND released_at IS NULL) OR
                (state = 2 AND runtime_ready_at IS NOT NULL AND
                    roster_transfer_committed_at IS NULL AND consumed_at IS NULL AND
                    terminal_at IS NULL AND released_at IS NULL) OR
                (state = 3 AND runtime_ready_at IS NOT NULL AND
                    roster_transfer_committed_at IS NOT NULL AND consumed_at IS NULL AND
                    terminal_at IS NULL AND released_at IS NULL) OR
                (state = 4 AND runtime_ready_at IS NOT NULL AND
                    roster_transfer_committed_at IS NOT NULL AND consumed_at IS NOT NULL AND
                    terminal_at IS NULL AND released_at IS NULL) OR
                (state IN (5, 6, 7) AND runtime_ready_at IS NOT NULL AND
                    roster_transfer_committed_at IS NOT NULL AND consumed_at IS NOT NULL AND
                    terminal_at IS NOT NULL AND released_at IS NULL) OR
                (state = 8 AND roster_transfer_committed_at IS NULL AND
                    consumed_at IS NULL AND terminal_at IS NULL AND
                    released_at IS NOT NULL) OR
                (state IN (9, 10, 11) AND runtime_ready_at IS NOT NULL AND
                    roster_transfer_committed_at IS NOT NULL AND
                    consumed_at IS NOT NULL AND terminal_at IS NOT NULL AND
                    released_at IS NULL AND cleanup_completed_at IS NOT NULL) OR
                (state = 12 AND roster_transfer_committed_at IS NULL AND
                    consumed_at IS NULL AND terminal_at IS NULL AND
                    released_at IS NOT NULL AND cleanup_completed_at IS NOT NULL)
            )
        );

        CREATE TABLE medusa_admission_foundation.members (
            admission_id uuid NOT NULL REFERENCES
                medusa_admission_foundation.admissions(admission_id),
            ordinal smallint NOT NULL CHECK (ordinal BETWEEN 0 AND 4),
            account_id integer NOT NULL CHECK (account_id > 0),
            character_id integer NOT NULL CHECK (character_id > 0),
            realm_id integer NOT NULL CHECK (realm_id > 0),
            player_level integer NOT NULL CHECK (player_level >= 90),
            source_world_instance_id uuid NOT NULL,
            source_map_id smallint NOT NULL CHECK (source_map_id >= 0),
            ownership_owner_id uuid NOT NULL,
            ownership_generation bigint NOT NULL CHECK (ownership_generation > 0),
            PRIMARY KEY (admission_id, ordinal),
            UNIQUE (admission_id, account_id),
            UNIQUE (admission_id, character_id)
        );

        CREATE TABLE medusa_admission_foundation.attempt_claims (
            realm_id integer NOT NULL CHECK (realm_id > 0),
            realm_day date NOT NULL,
            character_id integer NOT NULL CHECK (character_id > 0),
            admission_id uuid NOT NULL,
            claim_state smallint NOT NULL CHECK (claim_state IN (1, 2)),
            reserved_at timestamptz NOT NULL,
            consumed_at timestamptz NULL,
            PRIMARY KEY (realm_id, realm_day, character_id),
            UNIQUE (admission_id, character_id),
            FOREIGN KEY (admission_id, character_id) REFERENCES
                medusa_admission_foundation.members(admission_id, character_id),
            CHECK (
                (claim_state = 1 AND consumed_at IS NULL) OR
                (claim_state = 2 AND consumed_at IS NOT NULL AND
                    consumed_at >= reserved_at)
            )
        );

        -- Serializes one character across midnight and calendar revisions.
        -- Cleanup-completed transitions delete this assignment atomically;
        -- pending Released/terminal rows retain exact recovery/egress routing.
        CREATE TABLE medusa_admission_foundation.active_member_claims (
            realm_id integer NOT NULL CHECK (realm_id > 0),
            character_id integer NOT NULL CHECK (character_id > 0),
            admission_id uuid NOT NULL,
            reserved_at timestamptz NOT NULL,
            PRIMARY KEY (realm_id, character_id),
            UNIQUE (admission_id, character_id),
            FOREIGN KEY (admission_id, character_id) REFERENCES
                medusa_admission_foundation.members(admission_id, character_id)
        );

        CREATE TABLE medusa_admission_foundation.transition_receipts (
            transition_id uuid NOT NULL,
            admission_id uuid NOT NULL REFERENCES
                medusa_admission_foundation.admissions(admission_id),
            request_hash character(64) COLLATE "C" NOT NULL
                CHECK (request_hash ~ '^[0-9A-F]{64}$'),
            expected_state smallint NOT NULL CHECK (expected_state BETWEEN 1 AND 12),
            target_state smallint NOT NULL CHECK (target_state BETWEEN 1 AND 12),
            resulting_revision bigint NOT NULL CHECK (resulting_revision > 0),
            occurred_at timestamptz NOT NULL,
            recorded_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            PRIMARY KEY (admission_id, transition_id)
        );
        """;
}
