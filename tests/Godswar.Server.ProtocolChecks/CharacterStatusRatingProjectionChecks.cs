using Godswar.Server.State;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterStatusRatingProjectionChecks
{
    public const string CheckName =
        "Authoritative hostile-status rating projection";

    public static Task RunAsync()
    {
        var sql = PostgresCharacterRuntimeItemProjectionSql
            .CalculatedStatsForCharacter;
        Check.True(
            sql.Contains(
                "('State', 'status_hit', 1::numeric)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "('StateImmunity', 'status_resistance', 1::numeric)",
                StringComparison.Ordinal),
            "equipment State and StateImmunity project into runtime ratings");
        Check.True(
            sql.Contains("(11, 'status_hit')", StringComparison.Ordinal) &&
            sql.Contains(
                "(12, 'status_resistance')",
                StringComparison.Ordinal),
            "typed item attributes 11 and 12 project into runtime ratings");
        Check.True(
            sql.Contains(
                "('StatusHit', 'status_hit')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "('StatusMiss', 'status_resistance')",
                StringComparison.Ordinal) &&
            sql.Contains("talent_effective_rank", StringComparison.Ordinal),
            "duration-spell talents use effective rank for hostile-status ratings");
        Check.True(
            sql.Contains("AS status_hit", StringComparison.Ordinal) &&
            sql.Contains("AS status_resistance", StringComparison.Ordinal),
            "status ratings are returned by the calculated-stat projection");

        var stats = new CharacterStats
        {
            StatusHit = 321,
            StatusResistance = 123
        };
        Check.Equal(321, stats.StatusHit, "runtime status-hit stat");
        Check.Equal(
            123,
            stats.StatusResistance,
            "runtime status-resistance stat");
        var snapshot = FocusedGameplayProjectionCompatibility
            .ToApplication(stats);
        var hydrated = CharacterLoadSnapshotHydrator.MapCalculatedStats(
            snapshot);
        Check.True(
            snapshot.StatusHit == 321 &&
            snapshot.StatusResistance == 123 &&
            hydrated.StatusHit == 321 &&
            hydrated.StatusResistance == 123,
            "login snapshot preserves both hostile-status ratings");
        return Task.CompletedTask;
    }
}
