namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetBasicSavvyPreview() =>
        new(
            "20260812_080_pet_basic_savvy_preview",
            "Persist Fairy Feather previews and enforce aggregate Basic Savvy",
            """
            DO $migration$
            BEGIN
                IF EXISTS (
                    SELECT 1
                    FROM public.character_pets pet
                    LEFT JOIN LATERAL (
                        SELECT count(*)::integer AS stat_count,
                               count(stat.initial_savvy)::integer
                                    AS basic_count,
                               count(stat.birth_initial_savvy)::integer
                                    AS birth_count,
                               sum(stat.initial_savvy) AS basic_total,
                               sum(stat.birth_initial_savvy) AS birth_total
                        FROM public.character_pet_stat_values stat
                        WHERE stat.pet_id = pet.id
                    ) totals ON true
                    WHERE totals.stat_count <> 6
                       OR totals.basic_count <> 6
                       OR totals.birth_count <> 6
                       OR pet.initial_savvy_baseline_total IS NULL
                       OR totals.birth_total IS DISTINCT FROM
                            pet.initial_savvy_baseline_total
                       OR totals.basic_total < totals.birth_total
                ) THEN
                    RAISE EXCEPTION
                        'migration 080 requires complete aggregate Basic Savvy provenance';
                END IF;
            END
            $migration$;

            ALTER TABLE public.character_pet_stat_values
                DROP CONSTRAINT ck_pet_stat_savvy_progression;

            ALTER TABLE public.pet_operation_audit
                ADD CONSTRAINT ck_pet_operation_audit_operation_v5
                CHECK (
                    operation IN (
                        'owner_merge',
                        'pet_merge',
                        'rebirth',
                        'soul_contract',
                        'take',
                        'summon',
                        'dismiss',
                        'reveal_growth',
                        'reset_basic_savvy',
                        'seal',
                        'unseal',
                        'hatch',
                        'level_up'
                    )
                ) NOT VALID;

            ALTER TABLE public.pet_operation_audit
                VALIDATE CONSTRAINT
                    ck_pet_operation_audit_operation_v5;

            ALTER TABLE public.pet_operation_audit
                DROP CONSTRAINT pet_operation_audit_operation_check;

            ALTER TABLE public.pet_operation_audit
                RENAME CONSTRAINT
                    ck_pet_operation_audit_operation_v5
                TO pet_operation_audit_operation_check;

            CREATE OR REPLACE VIEW public.pet_durable_command_evidence AS
            SELECT
                inbox.id AS inbox_id,
                inbox.principal_key AS account_id,
                inbox.aggregate_key,
                inbox.command_family,
                encode(inbox.operation_id, 'hex') AS operation_id,
                inbox.result_code,
                inbox.duplicate_count,
                inbox.request_conflict_count,
                inbox.completed_at,
                audit.id AS audit_id,
                event.event_id,
                event.aggregate_version,
                event.delivered_at,
                event.poisoned_at
            FROM public.command_inbox inbox
            INNER JOIN public.command_audit audit
                ON audit.id = inbox.audit_id
            LEFT JOIN public.outbox_events event
                ON event.command_inbox_id = inbox.id
               AND event.consumer_key = 'pet_durable_v1'
            WHERE inbox.aggregate_type = 'character_pet_value'
              AND inbox.command_family IN (
                  'bag_item_activation',
                  'pet_level_upgrade',
                  'pet_presence_transition',
                  'pet_skill_unlearn',
                  'pet_growth_reset',
                  'pet_basic_savvy_reset',
                  'pet_owner_merge_toggle',
                  'pet_to_pet_merge',
                  'pet_rebirth'
              );

            CREATE OR REPLACE FUNCTION
                public.assert_character_pet_basic_savvy_aggregate(
                    target_pet_id bigint)
            RETURNS void
            LANGUAGE plpgsql
            AS $function$
            DECLARE
                baseline_total numeric;
                stat_count integer;
                basic_count integer;
                birth_count integer;
                basic_total numeric;
                birth_total numeric;
            BEGIN
                SELECT pet.initial_savvy_baseline_total
                INTO baseline_total
                FROM public.character_pets pet
                WHERE pet.id = target_pet_id;
                IF NOT FOUND THEN
                    RETURN;
                END IF;

                SELECT count(*)::integer,
                       count(stat.initial_savvy)::integer,
                       count(stat.birth_initial_savvy)::integer,
                       sum(stat.initial_savvy),
                       sum(stat.birth_initial_savvy)
                INTO stat_count, basic_count, birth_count,
                     basic_total, birth_total
                FROM public.character_pet_stat_values stat
                WHERE stat.pet_id = target_pet_id;

                IF stat_count <> 6 OR
                   basic_count <> 6 OR
                   birth_count <> 6 OR
                   baseline_total IS NULL OR
                   birth_total IS DISTINCT FROM baseline_total OR
                   basic_total < birth_total THEN
                    RAISE EXCEPTION
                        'pet % violates aggregate Basic Savvy provenance',
                        target_pet_id;
                END IF;
            END
            $function$;

            CREATE OR REPLACE FUNCTION
                public.enforce_pet_basic_savvy_stat_aggregate()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                IF TG_OP = 'UPDATE' AND
                   OLD.pet_id IS DISTINCT FROM NEW.pet_id THEN
                    PERFORM public.assert_character_pet_basic_savvy_aggregate(
                        OLD.pet_id);
                END IF;
                PERFORM public.assert_character_pet_basic_savvy_aggregate(
                    CASE WHEN TG_OP = 'DELETE' THEN OLD.pet_id
                         ELSE NEW.pet_id END);
                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END
            $function$;

            CREATE OR REPLACE FUNCTION
                public.enforce_pet_basic_savvy_parent_aggregate()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $function$
            BEGIN
                PERFORM public.assert_character_pet_basic_savvy_aggregate(
                    NEW.id);
                RETURN NEW;
            END
            $function$;

            CREATE CONSTRAINT TRIGGER
                ck_pet_stat_savvy_aggregate
            AFTER INSERT OR UPDATE OR DELETE
            ON public.character_pet_stat_values
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION
                public.enforce_pet_basic_savvy_stat_aggregate();

            CREATE CONSTRAINT TRIGGER
                ck_pet_parent_savvy_aggregate
            AFTER INSERT OR UPDATE
            ON public.character_pets
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW
            EXECUTE FUNCTION
                public.enforce_pet_basic_savvy_parent_aggregate();

            CREATE FUNCTION public.pet_basic_savvy_array_total(
                savvy_values numeric[])
            RETURNS numeric
            LANGUAGE sql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $function$
                SELECT sum(value) FROM unnest(savvy_values) value;
            $function$;

            CREATE FUNCTION public.pet_basic_savvy_array_has_hundredths(
                savvy_values numeric[])
            RETURNS boolean
            LANGUAGE sql
            IMMUTABLE
            STRICT
            PARALLEL SAFE
            AS $function$
                SELECT bool_and(value = round(value, 2))
                FROM unnest(savvy_values) value;
            $function$;

            CREATE TABLE public.character_pet_basic_savvy_previews (
                user_id integer PRIMARY KEY
                    REFERENCES public.character_base(id) ON DELETE CASCADE,
                pet_id bigint NOT NULL
                    REFERENCES public.character_pets(id) ON DELETE CASCADE,
                preview_operation_id uuid NOT NULL UNIQUE
                    CHECK (
                        preview_operation_id <>
                            '00000000-0000-0000-0000-000000000000'::uuid
                    ),
                connection_id uuid NOT NULL
                    CHECK (
                        connection_id <>
                            '00000000-0000-0000-0000-000000000000'::uuid
                    ),
                owner_id uuid NOT NULL
                    CHECK (
                        owner_id <>
                            '00000000-0000-0000-0000-000000000000'::uuid
                    ),
                owner_generation bigint NOT NULL
                    CHECK (owner_generation > 0),
                expected_pet_level smallint NOT NULL
                    CHECK (expected_pet_level BETWEEN 1 AND 120),
                expected_pet_revision bigint NOT NULL
                    CHECK (expected_pet_revision >= 0),
                expected_stat_revisions bigint[] NOT NULL
                    CHECK (
                        cardinality(expected_stat_revisions) = 6 AND
                        array_position(
                            expected_stat_revisions,
                            NULL) IS NULL AND
                        0 <= ALL(expected_stat_revisions)
                    ),
                expected_basic_total numeric(18,6) NOT NULL
                    CHECK (
                        expected_basic_total > 0 AND
                        expected_basic_total =
                            round(expected_basic_total, 2)
                    ),
                basic_savvy_values numeric(18,6)[] NOT NULL
                    CHECK (
                        cardinality(basic_savvy_values) = 6 AND
                        array_position(
                            basic_savvy_values,
                            NULL) IS NULL AND
                        0 < ALL(basic_savvy_values) AND
                        public.pet_basic_savvy_array_has_hundredths(
                            basic_savvy_values) AND
                        public.pet_basic_savvy_array_total(
                            basic_savvy_values) = expected_basic_total
                    ),
                policy_version varchar(64) NOT NULL
                    CHECK (policy_version = 'fairy-basic-savvy-v1'),
                roll_tier smallint NOT NULL
                    CHECK (roll_tier BETWEEN 1 AND 4),
                primary_focus smallint NOT NULL
                    CHECK (primary_focus BETWEEN 0 AND 6),
                secondary_focus smallint NOT NULL
                    CHECK (secondary_focus BETWEEN 0 AND 6),
                created_at timestamptz NOT NULL
                    DEFAULT transaction_timestamp(),
                expires_at timestamptz NOT NULL,
                CHECK (expires_at > created_at),
                CHECK (
                    (roll_tier IN (1, 2) AND
                     primary_focus BETWEEN 1 AND 6 AND
                     secondary_focus = 0)
                    OR
                    (roll_tier = 3 AND
                     primary_focus BETWEEN 1 AND 6 AND
                     secondary_focus BETWEEN 1 AND 6 AND
                     primary_focus <> secondary_focus)
                    OR
                    (roll_tier = 4 AND
                     primary_focus = 0 AND
                     secondary_focus = 0)
                )
            );

            CREATE INDEX ix_character_pet_basic_savvy_previews_expiry
                ON public.character_pet_basic_savvy_previews(expires_at);
            """);
}
