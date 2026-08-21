namespace Godswar.Server.State;

internal static partial class PostgresCharacterRuntimeItemProjectionSql
{
    private const string OwnerMergeInternalStatTotals =
        """
        COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'life_absorption_flat'), 0) AS life_absorption_flat,
        """;

    private const string OwnerMergeInternalStatSelects =
        """
        ROUND(COALESCE(stats.life_absorption_flat, 0))::integer AS life_absorption_flat
        """;
}
