using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresNpcDialogueMultiRouteMigrationChecks
{
    private const string MigrationId =
        "20260802_051_npc_dialogue_multi_route";
    private const string MigrationChecksum =
        "7C47C6464D0E7EA90A05003B6E3CC22F016DC8E49E734731584D0260CD18CB14";

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
            "NPC multi-route migration checksum is pinned");
        Check.Equal(
            "20260802_050_holy_suit_fixed_daily_cap",
            catalog[index - 1].Id,
            "NPC multi-route migration follows the existing schema history");
        Check.Equal(
            "20260802_052_class_suit_item_content",
            catalog[index + 1].Id,
            "Class Suit item content follows the NPC multi-route migration");

        var sql = migration.Sql;
        Check.True(
            sql.Contains(
                "CHECK (behavior BETWEEN 1 AND 5)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "ADD COLUMN route_order smallint NOT NULL DEFAULT 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "PRIMARY KEY (revision, npc_key, route_order)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (revision, npc_key, profile_key)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (route_order BETWEEN 0 AND 63)",
                StringComparison.Ordinal),
            "multi-route storage is bounded, ordered, and profile-unique");
        Check.True(
            sql.Contains(
                "MIN(binding.route_order) <> 0",
                StringComparison.Ordinal) &&
            sql.Contains(
                "MAX(binding.route_order) <> COUNT(*) - 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "has an unbound profile",
                StringComparison.Ordinal) &&
            sql.Contains(
                "GROUP BY binding.npc_key, profile.dialog_index",
                StringComparison.Ordinal) &&
            sql.Contains(
                "duplicates a client dialog endpoint",
                StringComparison.Ordinal),
            "publication rejects route gaps, unreachable profiles, and " +
            "duplicate client endpoints");
        Check.True(
            !sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase),
            "V2 migration preserves immutable V1 rows for rollback");
        return Task.CompletedTask;
    }
}
