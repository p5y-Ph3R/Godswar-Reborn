using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetPhoenixRebirthBracketMigrationChecks
{
    private const string MigrationId =
        "20260813_091_pet_phoenix_rebirth_bracket";

    public const string CheckName =
        "PostgreSQL Phoenix Rebirth bracket migration";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            value => value.Id == MigrationId);
        var sql = migration.Sql;
        foreach (var fragment in new[]
                 {
                     "ALTER TABLE public.character_pet_growth_previews",
                     "ADD COLUMN rate_semantics text NOT NULL",
                     "DEFAULT 'legacy_base_preserve_acceleration'",
                     "ADD COLUMN completed_rebirths smallint",
                     "ADD COLUMN rebirth_modifiers numeric(18,6)[]",
                     "'nature_base_rebirth_modifier_v1'",
                     "completed_rebirths IS NOT NULL",
                     "rebirth_modifiers IS NOT NULL",
                     "completed_rebirths BETWEEN 0 AND 100",
                     "array_ndims(rebirth_modifiers) = 1",
                     "array_lower(rebirth_modifiers, 1) = 1",
                     "array_upper(rebirth_modifiers, 1) = 6",
                     "cardinality(rebirth_modifiers) = 6",
                     "array_position(rebirth_modifiers, NULL) IS NULL",
                     "0.10 * completed_rebirths <=",
                     "0.20 * completed_rebirths >=",
                     "trunc(rebirth_modifiers[6] * 100)"
                 })
        {
            Check.True(
                sql.Contains(fragment, StringComparison.Ordinal),
                $"Phoenix Rebirth bracket migration contains {fragment}");
        }
        Check.True(
            !sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase),
            "Phoenix Rebirth bracket migration preserves old previews");
        return Task.CompletedTask;
    }
}
