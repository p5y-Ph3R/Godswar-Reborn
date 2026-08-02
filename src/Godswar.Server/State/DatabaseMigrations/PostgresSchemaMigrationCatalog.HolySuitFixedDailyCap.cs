namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateHolySuitFixedDailyCap() =>
        new(
            "20260802_050_holy_suit_fixed_daily_cap",
            "Add immutable fixed per-player Holy Suit daily EXP policy",
            """
            ALTER TABLE public.holy_suit_operation_policy_content_definitions
                ADD COLUMN daily_experience_per_player bigint;

            ALTER TABLE public.holy_suit_operation_policy_content_definitions
                DROP CONSTRAINT ck_holy_suit_operation_policy_values,
                ADD CONSTRAINT ck_holy_suit_operation_policy_values
                    CHECK (
                        minimum_player_level BETWEEN 1 AND 32767
                        AND minimum_gear_level BETWEEN 1 AND 32767
                        AND daily_experience_per_player_level > 0
                        AND (
                            daily_experience_per_player IS NULL
                            OR daily_experience_per_player
                                BETWEEN 1 AND 4294967295
                        )
                        AND per_operation_experience_maximum > 0
                        AND gear_experience_capacity >=
                            per_operation_experience_maximum
                        AND experience_prism_cost > 0
                        AND btrim(realm_day_time_zone) <> ''
                        AND btrim(daily_quota_bypass_entitlement) <> ''
                        AND btrim(source) <> ''
                    );

            COMMENT ON COLUMN public.holy_suit_operation_policy_content_definitions.daily_experience_per_player IS
                'Fixed daily player quota. NULL preserves legacy sealed '
                'level-scaled revisions for hash verification only.';

            CREATE OR REPLACE VIEW
                public.official_holy_suit_operation_policy_content
            WITH (security_barrier = true) AS
            SELECT definition.*
            FROM public.item_template_content_publication publication
            JOIN public.item_template_content_revisions release
              ON release.revision = publication.revision
             AND release.sealed_at IS NOT NULL
             AND release.manifest_version = 5
            JOIN public.holy_suit_operation_policy_content_definitions
                definition
              ON definition.revision = release.revision
            WHERE publication.family = 'items';
            """);
}
