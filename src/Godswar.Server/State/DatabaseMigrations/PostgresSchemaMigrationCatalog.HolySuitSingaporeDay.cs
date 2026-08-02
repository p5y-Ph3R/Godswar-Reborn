namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateHolySuitSingaporeDayBoundary() =>
        new(
            "20260802_049_holy_suit_singapore_day_boundary",
            "Document Singapore-local Holy Suit quota-day ownership",
            """
            COMMENT ON COLUMN
                public.holy_suit_daily_exp_storage.usage_day IS
                'Authoritative realm-local quota day resolved from the pinned Holy Suit policy. Legacy rows keep their original UTC key; alpha writes use Asia/Singapore (UTC+08:00).';
            """);
}
