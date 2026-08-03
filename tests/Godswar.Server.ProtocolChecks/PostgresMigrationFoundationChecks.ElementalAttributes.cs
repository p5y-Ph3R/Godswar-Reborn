using Godswar.Server.Infrastructure.Reconciliation;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckElementalClassSuitAttributeMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            static value => value.Id ==
                "20260803_054_elemental_class_suit_attributes");
        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN elemental_attribute1 smallint",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ADD COLUMN elemental_attribute2 smallint",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "deprecated class_attribute2 contains player value",
                StringComparison.Ordinal) &&
            CountOccurrences(
                migration.Sql,
                "canonical_character_item_state_v2(") >= 3 &&
            migration.Sql.Contains(
                "class_attribute2 IS NULL",
                StringComparison.Ordinal),
            "migration adds elemental slots and canonicalizes historical evidence before rejecting deprecated player value");
        Check.True(
            migration.Sql.Contains(
                "elemental_attribute1 BETWEEN 480 AND 500",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "elemental_attribute2 BETWEEN 480 AND 500",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "((elemental_attribute1 - 480) / 3) <>",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "item_grade BETWEEN 1 AND 25",
                StringComparison.Ordinal),
            "durable constraints enforce locked IDs, different elements, and grade bounds");
        Check.True(
            migration.Sql.Contains(
                "canonical_character_item_state_v3(item_state jsonb)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'elemental_attribute1'",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'elemental_attribute2'",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "'class_attribute2', COALESCE(",
                StringComparison.Ordinal),
            "canonical v3 owns elemental fields and does not preserve deprecated class slot two");
        Check.True(
            migration.Sql.Contains(
                "COALESCE(ci.elemental_attribute1::text, '')",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "COALESCE(ci.elemental_attribute2::text, '')",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "COALESCE(ci.class_attribute1::text, '') || ',' ||",
                StringComparison.Ordinal),
            "compact projection carries one class slot plus two elemental slots");
        Check.True(
            PostgresReconciliationSnapshot.CharacterPageSqlForChecks.Contains(
                "canonical_character_item_state_v3(",
                StringComparison.Ordinal) &&
            CountOccurrences(
                PostgresReconciliationSnapshot.LedgerChainSqlForChecks,
                "public.canonical_character_item_state_v3(") == 2,
            "runtime reconciliation uses canonical item schema v3 everywhere");
        Check.True(
            migration.Sql.Contains(
                "manifest_version IN (1, 2, 3, 4, 5, 6, 7)",
                StringComparison.Ordinal),
            "migration extends the immutable item manifest constraint through v7");
        Check.True(
            migration.Sql.Contains(
                "manifest_version IN (5, 6, 7)",
                StringComparison.Ordinal),
            "migration extends immutable publication guards through v7");
    }
}
