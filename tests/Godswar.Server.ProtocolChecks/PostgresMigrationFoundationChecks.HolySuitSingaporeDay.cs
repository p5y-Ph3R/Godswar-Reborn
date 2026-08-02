using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckHolySuitSingaporeDayMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(value =>
            value.Id ==
                "20260802_049_holy_suit_singapore_day_boundary");
        Check.True(
            migration.Sql.Contains(
                "alpha writes use Asia/Singapore (UTC+08:00)",
                StringComparison.Ordinal) &&
            migration.Sql.Contains(
                "Legacy rows keep their original UTC key",
                StringComparison.Ordinal) &&
            !migration.Sql.Contains(
                "INSERT ",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "UPDATE ",
                StringComparison.OrdinalIgnoreCase) &&
            !migration.Sql.Contains(
                "DELETE ",
                StringComparison.OrdinalIgnoreCase),
            "Singapore cutover preserves unsplittable historical UTC buckets");
    }
}
