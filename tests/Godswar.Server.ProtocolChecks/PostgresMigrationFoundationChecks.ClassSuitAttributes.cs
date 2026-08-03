using Godswar.Server.Infrastructure.Reconciliation;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckClassSuitAttributeSlotMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            value => value.Id ==
                "20260803_053_class_suit_attribute_slots");

        Check.True(
            migration.Sql.Contains(
                "ADD COLUMN class_attribute1 smallint",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ADD COLUMN class_attribute2 smallint",
                StringComparison.Ordinal),
            "Class Suit migration adds two dedicated durable fields");
        Check.True(
            migration.Sql.Contains(
                "cardinality(discovered.class_attributes) > 2",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "count(DISTINCT value.attribute_id)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "RAISE EXCEPTION",
                StringComparison.Ordinal),
            "Class Suit backfill rejects duplicate or overflowing legacy state");
        Check.True(
            migration.Sql.Contains(
                "class_attribute1 = reshaped.class_attributes[1]",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "attribute_level5 = reshaped.ordinary_levels[5]",
                StringComparison.Ordinal),
            "Class Suit backfill extracts special IDs and compacts paired ordinary levels");
        Check.True(
            migration.Sql.Contains(
                "ck_character_items_distinct_class_attributes",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "class_attribute2 IS NULL OR class_attribute1 IS NOT NULL",
                StringComparison.Ordinal),
            "Class Suit durable fields enforce canonical order and uniqueness");
        Check.True(
            migration.Sql.Contains(
                "canonical_character_item_state_v2(item_state jsonb)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "'class_attribute1', COALESCE(",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "reshaped.legacy_class_attributes[2]",
                StringComparison.Ordinal),
            "Class Suit migration publishes a schema-aware historical JSON normalizer");
        Check.True(
            PostgresReconciliationSnapshot.CharacterPageSqlForChecks.Contains(
                "canonical_character_item_state_v3(",
                StringComparison.Ordinal) &&
            PostgresReconciliationSnapshot.CharacterPageSqlForChecks.Contains(
                "to_jsonb(current_item)",
                StringComparison.Ordinal),
            "runtime reconciliation compares canonical historical and current item shapes");
        Check.True(
            CountOccurrences(
                PostgresReconciliationSnapshot.LedgerChainSqlForChecks,
                "public.canonical_character_item_state_v3(") == 2 &&
            PostgresReconciliationSnapshot.LedgerChainSqlForChecks.Contains(
                "row.before_state",
                StringComparison.Ordinal) &&
            PostgresReconciliationSnapshot.LedgerChainSqlForChecks.Contains(
                "THEN row.previous_state",
                StringComparison.Ordinal) &&
            PostgresReconciliationSnapshot.LedgerChainSqlForChecks.Contains(
                "ELSE baseline_item.item_state",
                StringComparison.Ordinal),
            "inventory ledger-chain reconciliation canonicalizes both schema versions without rewriting evidence");
        var eligibilityConstraint = migration.Sql.IndexOf(
            "ck_character_items_class_attribute_eligible_gear",
            StringComparison.Ordinal);
        var eligibilityStart = migration.Sql.IndexOf(
            "OR prop_id IN (",
            eligibilityConstraint,
            StringComparison.Ordinal) + "OR prop_id IN (".Length;
        var eligibilityEnd = migration.Sql.IndexOf(
            "));",
            eligibilityStart,
            StringComparison.Ordinal);
        var durableEligibleIds = migration.Sql[
                eligibilityStart..eligibilityEnd]
            .Split(',', StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries)
            .Select(uint.Parse)
            .Order()
            .ToArray();
        var catalogEligibleIds = ClassSuitConversionCatalog.Branches
            .SelectMany(static branch => new[]
            {
                branch.TierIIIItemId,
                branch.TierIVItemId
            })
            .Order()
            .ToArray();
        Check.True(
            durableEligibleIds.Length ==
                ClassSuitConversionCatalog.BranchCount * 2 &&
            durableEligibleIds.Distinct().Count() ==
                durableEligibleIds.Length &&
            durableEligibleIds.SequenceEqual(catalogEligibleIds),
            "durable eligibility is the reviewed unique Tier III/IV ID set for every branch");
        Check.True(
            migration.Sql.Contains(
                "public.character_item_compact_entries",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "LEFT JOIN public.official_item_template_content it",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "LEFT JOIN public.item_templates it",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "COALESCE(ci.holy_socket6_level::text, '') ||",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "COALESCE(ci.class_attribute2::text, '')",
                StringComparison.Ordinal),
            "compatibility view preserves immutable item-content authority, socket-six position, and Class Suit fields");
        Check.True(
            migration.Sql.Contains(
                "public.character_inventory_reconciliation",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "WHERE public.canonical_character_item_state_v2(",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "ELSE public.canonical_character_item_state_v2(",
                StringComparison.Ordinal),
            "report-only inventory reconciliation canonicalizes historical and current item schemas");
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   fragment,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }
}
