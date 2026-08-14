using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetPhoenixGrowthMigrationChecks
{
    public static Task RunAsync()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(value =>
            value.Id ==
                "20260811_071_pet_phoenix_growth_activation");
        Check.True(
            migration.Sql.Contains(
                "pet_phoenix_growth_activation_archive",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "sum(stat.base_growth_rate) NOT BETWEEN 0.01 AND 0.10",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "0.010000::numeric(18, 6)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "archive.old_added_savvy -",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "archive.old_base_growth_rate",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "NOT pet.growth_revealed",
                StringComparison.Ordinal),
            "Phoenix migration archives only out-of-Weak unrevealed Growth and preserves compatibility deltas");
        Check.True(
            migration.Sql.Contains(
                "revision = archive.old_stat_revision + 1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "revision = affected.old_pet_revision + 1",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "growth_activation_policy_version",
                StringComparison.Ordinal),
            "Phoenix migration advances revisions once and stamps its policy");
        return Task.CompletedTask;
    }
}
