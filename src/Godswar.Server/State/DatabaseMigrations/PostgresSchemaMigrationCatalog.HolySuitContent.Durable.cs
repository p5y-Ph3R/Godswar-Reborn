namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private const string HolySuitDurableStateSql = """
        CREATE TABLE public.account_entitlements (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            account_id integer NOT NULL,
            entitlement_key varchar(64) NOT NULL,
            scope_key varchar(64) NOT NULL DEFAULT 'global',
            starts_at timestamptz NOT NULL,
            expires_at timestamptz,
            revoked_at timestamptz,
            source varchar(64) NOT NULL,
            source_reference varchar(128),
            metadata jsonb NOT NULL DEFAULT '{}'::jsonb,
            created_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT fk_account_entitlements_account
                FOREIGN KEY (account_id)
                REFERENCES public.accounts (id)
                ON DELETE CASCADE,
            CONSTRAINT ck_account_entitlements_text
                CHECK (
                    btrim(entitlement_key) <> ''
                    AND btrim(scope_key) <> ''
                    AND btrim(source) <> ''
                    AND (source_reference IS NULL OR
                         btrim(source_reference) <> '')
                ),
            CONSTRAINT ck_account_entitlements_period
                CHECK (expires_at IS NULL OR expires_at > starts_at),
            CONSTRAINT ck_account_entitlements_metadata
                CHECK (jsonb_typeof(metadata) = 'object')
        );

        CREATE UNIQUE INDEX ux_account_entitlements_source_reference
            ON public.account_entitlements (source, source_reference)
            WHERE source_reference IS NOT NULL;
        CREATE INDEX ix_account_entitlements_active_lookup
            ON public.account_entitlements (
                account_id, entitlement_key, scope_key,
                starts_at, expires_at)
            WHERE revoked_at IS NULL;

        CREATE TABLE public.holy_suit_daily_exp_storage (
            account_id integer NOT NULL,
            realm_id integer NOT NULL,
            usage_day date NOT NULL,
            stored_exp bigint NOT NULL DEFAULT 0,
            operation_count integer NOT NULL DEFAULT 0,
            updated_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT pk_holy_suit_daily_exp_storage
                PRIMARY KEY (account_id, realm_id, usage_day),
            CONSTRAINT fk_holy_suit_daily_exp_storage_account
                FOREIGN KEY (account_id)
                REFERENCES public.accounts (id)
                ON DELETE CASCADE,
            CONSTRAINT fk_holy_suit_daily_exp_storage_realm
                FOREIGN KEY (realm_id)
                REFERENCES public.server (id)
                ON DELETE RESTRICT,
            CONSTRAINT ck_holy_suit_daily_exp_storage_values
                CHECK (stored_exp >= 0 AND operation_count >= 0)
        );

        CREATE INDEX ix_holy_suit_daily_exp_storage_usage_day
            ON public.holy_suit_daily_exp_storage (usage_day);

        CREATE OR REPLACE FUNCTION
            public.recompute_character_holy_suit_points(
                p_character_id integer)
        RETURNS integer
        LANGUAGE plpgsql
        AS $recompute_character_holy_suit_points$
        DECLARE
            calculated_points integer;
        BEGIN
            SELECT COALESCE(SUM(
                CASE
                    WHEN item.holy_suit_code > 0 THEN
                        LEAST(GREATEST(
                            item.holy_suit_code % 100, 0), 10)
                    ELSE 0
                END), 0)::integer
            INTO calculated_points
            FROM public.character_items item
            WHERE item.user_id = p_character_id
              AND item.item_location = 0
              AND item.slot_index BETWEEN 0 AND 11;

            UPDATE public.character_base character_row
            SET holy_suit_points = calculated_points
            WHERE character_row.id = p_character_id;
            IF NOT FOUND THEN
                RAISE EXCEPTION
                    'unknown character %', p_character_id
                    USING ERRCODE = '23503';
            END IF;

            RETURN calculated_points;
        END
        $recompute_character_holy_suit_points$;

        UPDATE public.character_base character_row
        SET holy_suit_points = COALESCE((
            SELECT SUM(
                CASE
                    WHEN item.holy_suit_code > 0 THEN
                        LEAST(GREATEST(
                            item.holy_suit_code % 100, 0), 10)
                    ELSE 0
                END)::integer
            FROM public.character_items item
            WHERE item.user_id = character_row.id
              AND item.item_location = 0
              AND item.slot_index BETWEEN 0 AND 11
        ), 0);

        COMMENT ON FUNCTION
            public.recompute_character_holy_suit_points(integer) IS
            'Explicitly recomputes the derived 0..120 Holy Suit effect points from equipped regular slots 0..11. Call after committed equipment mutations; no hidden trigger is installed.';

        """;
}
