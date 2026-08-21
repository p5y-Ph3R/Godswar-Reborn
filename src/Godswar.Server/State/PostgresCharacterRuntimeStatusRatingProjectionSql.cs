namespace Godswar.Server.State;

internal static partial class PostgresCharacterRuntimeItemProjectionSql
{
    private const string EquipmentStatusRatingValues =
        """
        ('State', 'status_hit', 1::numeric),
        ('StateImmunity', 'status_resistance', 1::numeric),
        """;

    private const string AttributeStatusRatingValues =
        """
        (11, 'status_hit'), (12, 'status_resistance'),
        """;

    private const string TalentStatusRatingValues =
        """
        ('StatusHit', 'status_hit'),
        ('StatusMiss', 'status_resistance'),
        """;

    private const string StatusRatingTotals =
        """
        COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'status_hit'), 0) AS status_hit,
        COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'status_resistance'), 0) AS status_resistance,
        """;

    private const string StatusRatingSelects =
        """
        ROUND(COALESCE(stats.status_hit, 0))::integer AS status_hit,
        ROUND(COALESCE(stats.status_resistance, 0))::integer AS status_resistance
        """;
}
