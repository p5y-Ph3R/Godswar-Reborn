using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresInboxOutboxHardeningMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_025_command_inbox_outbox_foundation";
    private const string PreviousMigrationChecksum =
        "7213DCEE445B6D577C0281E22242EDA1EA84E72167B3DB8631C8F86102D3D007";
    private const string MigrationId =
        "20260729_026_command_inbox_outbox_hardening";
    private const string MigrationChecksum =
        "8BA9B0B0136429140610FEC13BC58F8DD45CD1C6306317FB849271163EB59709";
    private const string NextMigrationId =
        "20260729_027_economy_ledger_foundation";

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
            MigrationChecksum,
            migration.Checksum,
            "command inbox/outbox hardening checksum is pinned");
        Check.Equal(
            PreviousMigrationId,
            catalog[index - 1].Id,
            "command inbox/outbox hardening follows its immutable foundation");
        Check.Equal(
            PreviousMigrationChecksum,
            catalog[index - 1].Checksum,
            "command inbox/outbox hardening preserves its applied predecessor");
        Check.Equal(
            NextMigrationId,
            catalog[index + 1].Id,
            "command inbox/outbox hardening has the expected economy successor");

        CheckEventIdentity(migration.Sql);
        CheckAggregateKeys(migration.Sql);
        CheckEventStateMachine(migration.Sql);
        CheckConsumerPositionGuard(migration.Sql);
        CheckLeaseConsistency(migration.Sql);
        CheckNonDestructiveDataChanges(migration.Sql);
        return Task.CompletedTask;
    }

    private static void CheckEventIdentity(string sql)
    {
        Check.True(
            sql.Contains(
                "ck_outbox_events_event_id_not_empty",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'00000000-0000-0000-0000-000000000000'::uuid",
                StringComparison.Ordinal),
            "outbox event IDs reject the empty UUID at the database boundary");
    }

    private static void CheckAggregateKeys(string sql)
    {
        Check.True(
            sql.Contains(
                "ck_command_audit_aggregate_key_no_control",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_command_inbox_aggregate_key_no_control",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_outbox_events_aggregate_key_no_control",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ck_outbox_positions_aggregate_key_no_control",
                StringComparison.Ordinal) &&
            Count(sql, "aggregate_key !~ '[[:cntrl:]]'") == 4,
            "command and outbox aggregate keys reject control characters");
    }

    private static void CheckConsumerPositionGuard(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE OR REPLACE FUNCTION",
                StringComparison.Ordinal) &&
            sql.Contains(
                "IF TG_OP = 'DELETE' THEN",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox consumer positions cannot be deleted.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox consumer position cannot move backwards.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "An idle outbox consumer position cannot advance.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "An outbox checkpoint must use its inflight version.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "An active outbox position lease cannot be retargeted.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "New outbox consumer positions must start idle at zero.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "DROP TRIGGER trg_outbox_consumer_positions_guard",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE INSERT OR UPDATE OR DELETE",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ON public.outbox_consumer_positions",
                StringComparison.Ordinal),
            "consumer positions reject deletion while retaining monotonic updates");
    }

    private static void CheckEventStateMachine(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE OR REPLACE FUNCTION",
                StringComparison.Ordinal) &&
            sql.Contains(
                "public.guard_outbox_event_mutation()",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Terminal outbox events are immutable.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox attempts advance once when a lease is acquired.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "An outbox lease must consume exactly one attempt.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "An active outbox lease cannot be retargeted.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "New outbox events must start pending and unleased.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE INSERT OR UPDATE OR DELETE ON public.outbox_events",
                StringComparison.Ordinal),
            "outbox event attempts, active leases, and terminal states are guarded");
    }

    private static void CheckLeaseConsistency(string sql)
    {
        Check.True(
            sql.Contains(
                "public.guard_outbox_lease_consistency()",
                StringComparison.Ordinal) &&
            sql.Contains(
                "trg_outbox_events_lease_consistency",
                StringComparison.Ordinal) &&
            sql.Contains(
                "trg_outbox_positions_lease_consistency",
                StringComparison.Ordinal) &&
            Count(sql, "DEFERRABLE INITIALLY DEFERRED") == 2 &&
            sql.Contains(
                "Outbox position and event leases must match.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Existing outbox lease or checkpoint state is inconsistent.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "An outbox checkpoint requires a delivered inflight event.",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Outbox event and position final state must match.",
                StringComparison.Ordinal),
            "event and consumer-position leases are validated together at commit");
    }

    private static void CheckNonDestructiveDataChanges(string sql)
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
            "hardening changes constraints and a trigger without deleting data");
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
