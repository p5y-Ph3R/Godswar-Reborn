using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresEconomyLedgerMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_026_command_inbox_outbox_hardening";
    private const string PreviousMigrationChecksum =
        "8BA9B0B0136429140610FEC13BC58F8DD45CD1C6306317FB849271163EB59709";
    private const string FoundationMigrationId =
        "20260729_027_economy_ledger_foundation";
    private const string FoundationMigrationChecksum =
        "EBDC2A157F6D1900AB35BB61A68C4BE7F97F8B707B9A21EEA644EE64A754C05F";
    private const string HardeningMigrationId =
        "20260729_028_economy_ledger_hardening";
    private const string HardeningMigrationChecksum =
        "1EDBA2FFF56F6A5DFB4C17B00BF909D68F915D8B8183ACA44100BACC8BF4B544";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var foundationIndex = Find(catalog, FoundationMigrationId);
        var hardeningIndex = Find(catalog, HardeningMigrationId);
        var foundation = catalog[foundationIndex];
        var hardening = catalog[hardeningIndex];

        Check.Equal(
            PreviousMigrationId,
            catalog[foundationIndex - 1].Id,
            "economy foundation follows command inbox/outbox hardening");
        Check.Equal(
            PreviousMigrationChecksum,
            catalog[foundationIndex - 1].Checksum,
            "economy foundation preserves its applied predecessor");
        Check.Equal(
            HardeningMigrationId,
            catalog[foundationIndex + 1].Id,
            "economy foundation has its exact hardening successor");
        Check.Equal(
            foundationIndex + 1,
            hardeningIndex,
            "economy foundation and hardening remain adjacent");
        Check.Equal(
            "20260730_029_holy_stone_material_templates",
            catalog[hardeningIndex + 1].Id,
            "Holy Stone material content follows economy hardening");
        Check.Equal(
            FoundationMigrationChecksum,
            foundation.Checksum,
            "economy foundation checksum is pinned");
        Check.Equal(
            HardeningMigrationChecksum,
            hardening.Checksum,
            "economy hardening checksum is pinned");

        CheckAggregateRevisions(foundation.Sql);
        CheckOpeningBaselines(foundation.Sql);
        CheckCurrencyLedger(foundation.Sql);
        CheckInventoryLedger(foundation.Sql);
        CheckBaselineBackfill(foundation.Sql);
        CheckInventoryDomains(hardening.Sql);
        CheckImmutableEvidence(hardening.Sql);
        CheckReconciliationViews(hardening.Sql);
        CheckNonDestructive(foundation.Sql, hardening.Sql);
        return Task.CompletedTask;
    }

    private static int Find(
        IReadOnlyList<PostgresSchemaMigration> catalog,
        string id) =>
        catalog
            .Select((migration, index) => (migration, index))
            .Single(entry => entry.migration.Id == id)
            .index;

    private static void CheckAggregateRevisions(string sql)
    {
        Check.True(
            sql.Contains(
                "ADD COLUMN wallet_revision bigint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD COLUMN inventory_revision bigint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (wallet_revision >= 0)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (inventory_revision >= 0)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (\"Money\" BETWEEN 0 AND 2147483647)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (\"Stone\" BETWEEN 0 AND 2147483647)",
                StringComparison.Ordinal),
            "character wallet and inventory aggregates have durable bounded revisions");
    }

    private static void CheckOpeningBaselines(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.character_economy_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (character_id, account_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "wallet_revision = 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "inventory_revision = 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "baseline_source ~ '^[a-z][a-z0-9_.-]{0,63}$'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CREATE TABLE public.character_inventory_baseline_items",
                StringComparison.Ordinal) &&
            sql.Contains(
                "octet_length(item_state::text) <= 8192",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.character_economy_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ON DELETE RESTRICT",
                StringComparison.Ordinal) &&
            !sql.Contains("pgcrypto", StringComparison.OrdinalIgnoreCase),
            "opening wallet and per-item snapshots are bounded and need no extension");
    }

    private static void CheckCurrencyLedger(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.character_currency_ledger",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (command_inbox_id, currency_code)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "wallet_revision,",
                StringComparison.Ordinal) &&
            sql.Contains(
                "currency_code IN ('silver', 'gold')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "balance_after = balance_before + delta",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.command_inbox (id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "fk_character_currency_ledger_baseline",
                StringComparison.Ordinal),
            "currency evidence is command-linked, revisioned, arithmetic, and baseline-owned");
    }

    private static void CheckInventoryLedger(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.character_inventory_ledger",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (command_inbox_id, entry_ordinal)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "inventory_revision,",
                StringComparison.Ordinal) &&
            sql.Contains(
                "entry_ordinal BETWEEN 0 AND 255",
                StringComparison.Ordinal) &&
            sql.Contains(
                "mutation_kind IN ('add', 'update', 'move', 'delete')",
                StringComparison.Ordinal) &&
            Count(sql, "octet_length(") >= 3 &&
            sql.Contains(
                "fk_character_inventory_ledger_inbox",
                StringComparison.Ordinal) &&
            sql.Contains(
                "fk_character_inventory_ledger_baseline",
                StringComparison.Ordinal),
            "inventory evidence is command-linked, ordered, bounded, and baseline-owned");
    }

    private static void CheckBaselineBackfill(string sql)
    {
        Check.True(
            sql.Contains(
                "INSERT INTO public.character_economy_baseline",
                StringComparison.Ordinal) &&
            sql.Contains(
                "LEFT JOIN public.character_items",
                StringComparison.Ordinal) &&
            sql.Contains(
                "'migration_027'",
                StringComparison.Ordinal) &&
            sql.Contains(
                "INSERT INTO public.character_inventory_baseline_items",
                StringComparison.Ordinal) &&
            sql.Contains(
                "to_jsonb(item_row)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "Character inventory baseline snapshot count mismatch.",
                StringComparison.Ordinal),
            "migration records exact opening balances and item snapshots before cutover");
    }

    private static void CheckInventoryDomains(string sql)
    {
        Check.True(
            sql.Contains(
                "ck_character_items_location_slot_domain",
                StringComparison.Ordinal) &&
            sql.Contains(
                "item_location = 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "slot_index BETWEEN 0 AND 23",
                StringComparison.Ordinal) &&
            sql.Contains(
                "item_location = 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "slot_index BETWEEN 0 AND 32767",
                StringComparison.Ordinal) &&
            sql.Contains(
                "item_location = 2",
                StringComparison.Ordinal) &&
            sql.Contains(
                "slot_index BETWEEN -32768 AND -1",
                StringComparison.Ordinal) &&
            sql.Contains("CHECK (stack > 0)", StringComparison.Ordinal) &&
            sql.Contains("CHECK (item_exp >= 0)", StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (item_quality BETWEEN 0 AND 20)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (item_grade BETWEEN 0 AND 25)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (bound BETWEEN 0 AND 1)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (holy_socket_count BETWEEN 0 AND 6)",
                StringComparison.Ordinal),
            "persisted item domains retain legacy bag rows and bound active values");
    }

    private static void CheckImmutableEvidence(string sql)
    {
        Check.True(
            sql.Contains(
                "public.reject_character_economy_evidence_mutation()",
                StringComparison.Ordinal) &&
            Count(sql, "BEFORE UPDATE OR DELETE") == 4 &&
            Count(sql, "BEFORE TRUNCATE") == 4 &&
            Count(
                sql,
                "public.reject_character_economy_evidence_mutation();") == 8,
            "all four evidence tables reject updates, deletes, and truncation");
    }

    private static void CheckReconciliationViews(string sql)
    {
        Check.True(
            sql.Contains(
                "public.character_wallet_reconciliation",
                StringComparison.Ordinal) &&
            sql.Contains(
                "public.character_inventory_reconciliation",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision_sequence_contiguous",
                StringComparison.Ordinal) &&
            sql.Contains(
                "mismatched_item_count",
                StringComparison.Ordinal) &&
            Count(sql, "AS is_reconciled") == 2 &&
            Count(sql, "Report-only") == 2,
            "report-only views expose wallet and inventory reconciliation failures");
    }

    private static void CheckNonDestructive(
        string foundationSql,
        string hardeningSql)
    {
        var sql = foundationSql + "\n" + hardeningSql;
        Check.True(
            !sql.Contains(
                "DELETE FROM",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "TRUNCATE TABLE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DROP TABLE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DROP COLUMN",
                StringComparison.OrdinalIgnoreCase),
            "economy migrations preserve authoritative player value");
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
