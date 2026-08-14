using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresBagConsumableCooldownMigrationChecks
{
    private const string MigrationId =
        "20260813_090_bag_consumable_cooldown_state";

    public const string CheckName =
        "PostgreSQL bag-consumable cooldown migration";

    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(
            value => value.Id == MigrationId);
        var sql = migration.Sql;
        foreach (var fragment in new[]
                 {
                     "CREATE TABLE public.character_bag_consumable_cooldowns",
                     "REFERENCES public.character_base(id)",
                     "ON DELETE CASCADE",
                     "PRIMARY KEY (character_id, cooldown_group)",
                     "CHECK (cooldown_group > 0)",
                     "CHECK (ready_at >= updated_at)"
                 })
        {
            Check.True(
                sql.Contains(fragment, StringComparison.Ordinal),
                $"cooldown migration contains {fragment}");
        }
        Check.True(
            !sql.Contains("UNLOGGED", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DROP TABLE", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("DELETE FROM", StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains("TRUNCATE", StringComparison.OrdinalIgnoreCase),
            "cooldown migration is durable, additive, and non-destructive");
        return Task.CompletedTask;
    }
}
