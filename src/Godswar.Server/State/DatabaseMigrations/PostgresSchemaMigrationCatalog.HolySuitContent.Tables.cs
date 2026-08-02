namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string HolySuitContentTablesSql = """
        CREATE TABLE public.holy_suit_tier_content_definitions (
            revision varchar(64) NOT NULL,
            suit_type smallint NOT NULL,
            display_name varchar(32) NOT NULL,
            max_level smallint NOT NULL,
            ware_item_id integer,
            source varchar(128) NOT NULL,
            CONSTRAINT pk_holy_suit_tier_content
                PRIMARY KEY (revision, suit_type),
            CONSTRAINT fk_holy_suit_tier_revision
                FOREIGN KEY (revision)
                REFERENCES public.item_template_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_holy_suit_tier_ware_template
                FOREIGN KEY (revision, ware_item_id)
                REFERENCES public.item_template_content_definitions
                    (revision, id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_holy_suit_tier_shape
                CHECK (
                    (suit_type = 0 AND max_level = 0
                     AND ware_item_id IS NULL)
                    OR
                    (suit_type BETWEEN 1 AND 7 AND max_level = 10
                     AND ware_item_id IS NOT NULL)
                ),
            CONSTRAINT ck_holy_suit_tier_text
                CHECK (btrim(display_name) <> '' AND btrim(source) <> '')
        );

        CREATE TABLE public.holy_suit_consumable_content_definitions (
            revision varchar(64) NOT NULL,
            item_id integer NOT NULL,
            role varchar(32) NOT NULL,
            suit_type smallint,
            experience_capacity bigint NOT NULL,
            stack_cap smallint NOT NULL,
            granted_bound smallint NOT NULL,
            source varchar(128) NOT NULL,
            CONSTRAINT pk_holy_suit_consumable_content
                PRIMARY KEY (revision, item_id),
            CONSTRAINT fk_holy_suit_consumable_revision
                FOREIGN KEY (revision)
                REFERENCES public.item_template_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_holy_suit_consumable_template
                FOREIGN KEY (revision, item_id)
                REFERENCES public.item_template_content_definitions
                    (revision, id)
                ON DELETE RESTRICT,
            CONSTRAINT fk_holy_suit_consumable_tier
                FOREIGN KEY (revision, suit_type)
                REFERENCES public.holy_suit_tier_content_definitions
                    (revision, suit_type)
                ON DELETE RESTRICT,
            CONSTRAINT ck_holy_suit_consumable_common
                CHECK (
                    item_id > 0
                    AND stack_cap BETWEEN 1 AND 32767
                    AND granted_bound IN (0, 1)
                    AND experience_capacity >= 0
                    AND btrim(source) <> ''
                ),
            CONSTRAINT ck_holy_suit_consumable_shape
                CHECK (
                    (role = 'ware' AND suit_type BETWEEN 1 AND 7
                     AND experience_capacity = 0 AND stack_cap = 99)
                    OR
                    (role = 'holy_box' AND suit_type IS NULL
                     AND experience_capacity > 0 AND stack_cap = 1)
                    OR
                    (role = 'experience_prism' AND suit_type IS NULL
                     AND experience_capacity = 100000000
                     AND stack_cap = 99)
                )
        );

        CREATE TABLE public.holy_suit_upgrade_content_definitions (
            revision varchar(64) NOT NULL,
            current_suit_type smallint NOT NULL,
            current_level smallint NOT NULL,
            target_suit_type smallint NOT NULL,
            target_level smallint NOT NULL,
            required_item_experience bigint NOT NULL,
            ware_item_id integer NOT NULL,
            ware_quantity smallint NOT NULL,
            required_prisms integer NOT NULL,
            source varchar(128) NOT NULL,
            CONSTRAINT pk_holy_suit_upgrade_content
                PRIMARY KEY (revision, current_suit_type, current_level),
            CONSTRAINT fk_holy_suit_upgrade_revision
                FOREIGN KEY (revision)
                REFERENCES public.item_template_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT fk_holy_suit_upgrade_current_tier
                FOREIGN KEY (revision, current_suit_type)
                REFERENCES public.holy_suit_tier_content_definitions
                    (revision, suit_type)
                ON DELETE RESTRICT,
            CONSTRAINT fk_holy_suit_upgrade_target_tier
                FOREIGN KEY (revision, target_suit_type)
                REFERENCES public.holy_suit_tier_content_definitions
                    (revision, suit_type)
                ON DELETE RESTRICT,
            CONSTRAINT fk_holy_suit_upgrade_ware
                FOREIGN KEY (revision, ware_item_id)
                REFERENCES public.holy_suit_consumable_content_definitions
                    (revision, item_id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_holy_suit_upgrade_state
                CHECK (
                    ((current_suit_type = 0 AND current_level = 0)
                     OR (current_suit_type BETWEEN 1 AND 7
                         AND current_level BETWEEN 1 AND 10))
                    AND target_suit_type BETWEEN 1 AND 7
                    AND target_level BETWEEN 1 AND 10
                ),
            CONSTRAINT ck_holy_suit_upgrade_cost
                CHECK (
                    required_item_experience >= 0
                    AND required_prisms >= 0
                    AND ((required_item_experience > 0) <>
                         (required_prisms > 0))
                    AND ware_quantity = target_level
                    AND btrim(source) <> ''
                )
        );

        CREATE TABLE public.holy_suit_operation_policy_content_definitions (
            revision varchar(64) NOT NULL,
            policy_key varchar(32) NOT NULL,
            minimum_player_level smallint NOT NULL,
            minimum_gear_level smallint NOT NULL,
            daily_experience_per_player_level bigint NOT NULL,
            per_operation_experience_maximum bigint NOT NULL,
            gear_experience_capacity bigint NOT NULL,
            experience_prism_cost bigint NOT NULL,
            realm_day_time_zone varchar(64) NOT NULL,
            daily_quota_bypass_entitlement varchar(64) NOT NULL,
            source varchar(128) NOT NULL,
            CONSTRAINT pk_holy_suit_operation_policy_content
                PRIMARY KEY (revision, policy_key),
            CONSTRAINT fk_holy_suit_operation_policy_revision
                FOREIGN KEY (revision)
                REFERENCES public.item_template_content_revisions (revision)
                ON DELETE RESTRICT,
            CONSTRAINT ck_holy_suit_operation_policy_key
                CHECK (policy_key = 'alpha'),
            CONSTRAINT ck_holy_suit_operation_policy_values
                CHECK (
                    minimum_player_level BETWEEN 1 AND 32767
                    AND minimum_gear_level BETWEEN 1 AND 32767
                    AND daily_experience_per_player_level > 0
                    AND per_operation_experience_maximum > 0
                    AND gear_experience_capacity >=
                        per_operation_experience_maximum
                    AND experience_prism_cost > 0
                    AND btrim(realm_day_time_zone) <> ''
                    AND btrim(daily_quota_bypass_entitlement) <> ''
                    AND btrim(source) <> ''
                )
        );

        """;
}
