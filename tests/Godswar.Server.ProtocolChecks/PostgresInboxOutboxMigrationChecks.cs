using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresInboxOutboxMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_024_npc_dialogue_content_release";
    private const string MigrationId =
        "20260729_025_command_inbox_outbox_foundation";
    private const string MigrationChecksum =
        "7213DCEE445B6D577C0281E22242EDA1EA84E72167B3DB8631C8F86102D3D007";
    private const string NextMigrationId =
        "20260729_026_command_inbox_outbox_hardening";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var index = catalog
            .Select((migration, migrationIndex) =>
                (migration, migrationIndex))
            .Single(entry => entry.migration.Id == MigrationId)
            .migrationIndex;
        var migration = catalog[index];

        Check.Equal(
            catalog.Count - 2,
            index,
            "command inbox/outbox foundation immediately precedes hardening");
        Check.Equal(
            MigrationChecksum,
            migration.Checksum,
            "command inbox/outbox foundation checksum is pinned");
        Check.Equal(
            PreviousMigrationId,
            catalog[index - 1].Id,
            "command inbox/outbox follows immutable NPC dialogue content");
        Check.Equal(
            NextMigrationId,
            catalog[index + 1].Id,
            "command inbox/outbox foundation has the expected hardening successor");

        CheckTalentAggregateRevision(migration.Sql);
        CheckCommandAudit(migration.Sql);
        CheckCommandInbox(migration.Sql);
        CheckVersionedOutbox(migration.Sql);
        CheckConsumerPositions(migration.Sql);
        CheckImmutability(migration.Sql);
        CheckNonDestructive(migration.Sql);
        return Task.CompletedTask;
    }

    private static void CheckTalentAggregateRevision(string sql)
    {
        Check.True(
            sql.Contains(
                "ALTER TABLE public.character_talents",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD COLUMN outbox_revision bigint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (outbox_revision >= 0)",
                StringComparison.Ordinal),
            "talent aggregate event versions start from a bounded durable revision");
    }

    private static void CheckCommandAudit(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.command_audit",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CONSTRAINT uq_command_audit_operation UNIQUE (",
                StringComparison.Ordinal) &&
            HasOperationIdentity(sql) &&
            Count(sql, "octet_length(request_hash) = 32") >= 2 &&
            sql.Contains(
                "octet_length(detail_payload::text) <= 16384",
                StringComparison.Ordinal) &&
            sql.Contains(
                "retention_policy = 'permanent'",
                StringComparison.Ordinal),
            "command audit is bounded, operation-scoped, and permanently retained");
    }

    private static void CheckCommandInbox(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.command_inbox",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CONSTRAINT uq_command_inbox_operation UNIQUE (",
                StringComparison.Ordinal) &&
            HasOperationIdentity(sql) &&
            sql.Contains(
                "octet_length(operation_id) BETWEEN 16 AND 64",
                StringComparison.Ordinal) &&
            sql.Contains(
                "result_contract_version BETWEEN 1 AND 32767",
                StringComparison.Ordinal) &&
            sql.Contains(
                "octet_length(result_payload::text) <= 16384",
                StringComparison.Ordinal) &&
            sql.Contains(
                "octet_length(result_hash) = 32",
                StringComparison.Ordinal),
            "command inbox identity, hashes, result version, and result are bounded");
        Check.True(
            sql.Contains(
                "CONSTRAINT uq_command_inbox_audit UNIQUE (audit_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.command_audit (id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "duplicate_count BETWEEN 0 AND 1000000",
                StringComparison.Ordinal) &&
            sql.Contains(
                "request_conflict_count BETWEEN 0 AND 1000000",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE INDEX ix_command_inbox_completed",
                StringComparison.Ordinal),
            "inbox audit linkage, conflict counters, and operational lookup are bounded");
    }

    private static void CheckVersionedOutbox(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.outbox_events",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CONSTRAINT uq_outbox_events_event_id UNIQUE (event_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CONSTRAINT uq_outbox_events_version UNIQUE (",
                StringComparison.Ordinal) &&
            sql.Contains(
                "aggregate_version > 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "contract_version BETWEEN 1 AND 32767",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ordering_policy IN ('strict', 'latest_wins')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "octet_length(payload::text) <= 16384",
                StringComparison.Ordinal),
            "outbox event identity, aggregate version, policy, contract, and payload are bounded");
        Check.True(
            sql.Contains(
                "attempt_count BETWEEN 0 AND 100",
                StringComparison.Ordinal) &&
            sql.Contains(
                "max_attempts BETWEEN 1 AND 100",
                StringComparison.Ordinal) &&
            sql.Contains(
                "lease_owner varchar(128)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "lease_token uuid",
                StringComparison.Ordinal) &&
            sql.Contains(
                "lease_expires_at timestamptz",
                StringComparison.Ordinal) &&
            sql.Contains(
                "delivered_at timestamptz",
                StringComparison.Ordinal) &&
            sql.Contains(
                "poisoned_at timestamptz",
                StringComparison.Ordinal),
            "outbox attempts, availability, lease, delivery, and poison states are explicit");
        Check.True(
            sql.Contains(
                "CREATE INDEX ix_outbox_events_pending",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE INDEX ix_outbox_events_expired_lease",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE INDEX ix_outbox_events_poison",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE INDEX ix_outbox_events_inbox",
                StringComparison.Ordinal),
            "outbox polling, lease recovery, poison, and command lookups are indexed");
    }

    private static void CheckConsumerPositions(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.outbox_consumer_positions",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CONSTRAINT pk_outbox_consumer_positions PRIMARY KEY (",
                StringComparison.Ordinal) &&
            sql.Contains(
                "current_version bigint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "inflight_event_id bigint",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CONSTRAINT fk_outbox_positions_inflight",
                StringComparison.Ordinal) &&
            sql.Contains(
                "inflight_version > current_version",
                StringComparison.Ordinal) &&
            sql.Contains(
                "inflight_version - current_version = 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE INDEX ix_outbox_consumer_positions_lease",
                StringComparison.Ordinal),
            "consumer positions retain bounded version and in-flight lease state");
    }

    private static void CheckImmutability(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TRIGGER trg_command_audit_immutable",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE UPDATE OR DELETE ON public.command_audit",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TRIGGER trg_command_inbox_guard",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Command inbox identity and result are immutable.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Command inbox counters cannot decrease.",
                StringComparison.Ordinal),
            "audit and inbox identity/results cannot be rewritten or removed");
        Check.True(
            sql.Contains(
                "CREATE TRIGGER trg_outbox_events_guard",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox event identity and payload are immutable.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox attempt count cannot decrease.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TRIGGER trg_outbox_consumer_positions_guard",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox consumer position cannot move backwards.",
                StringComparison.Ordinal),
            "outbox payloads remain immutable and delivery positions cannot regress");
    }

    private static void CheckNonDestructive(string sql)
    {
        Check.True(
            !sql.Contains(
                "DROP TABLE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DROP COLUMN",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "TRUNCATE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DELETE FROM",
                StringComparison.OrdinalIgnoreCase),
            "command inbox/outbox foundation contains no destructive data operation");
    }

    private static bool HasOperationIdentity(string sql) =>
        HasOrderedFragments(
            sql,
            "principal_type",
            "principal_key",
            "aggregate_type",
            "aggregate_key",
            "command_family",
            "operation_id");

    private static bool HasOrderedFragments(
        string value,
        params string[] fragments)
    {
        var offset = 0;
        foreach (var fragment in fragments)
        {
            var found = value.IndexOf(
                fragment,
                offset,
                StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            offset = found + fragment.Length;
        }

        return true;
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while (true)
        {
            var next = value.IndexOf(
                fragment,
                offset,
                StringComparison.Ordinal);
            if (next < 0)
            {
                return count;
            }

            count++;
            offset = next + fragment.Length;
        }
    }
}
