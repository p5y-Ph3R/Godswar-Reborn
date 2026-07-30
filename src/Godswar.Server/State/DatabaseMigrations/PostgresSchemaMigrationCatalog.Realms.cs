namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateTempestRealmAuthority() => new(
            "20260731_035_tempest_realm_authority",
            "Make the legacy Tempest server row authoritative as realm one",
            """
            DO $tempest_realm_preflight$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                    FROM public.server realm
                    WHERE realm.id = 1
                      AND realm.name = 'Tempest'
                      AND realm.identifier =
                          'KAL3jcIzqGgKvOf1dbYZKC8cS'
                ) THEN
                    RAISE EXCEPTION
                        'Tempest realm identity is missing or conflicts with realm id 1.'
                        USING ERRCODE = '23514';
                END IF;

                IF EXISTS (
                    SELECT 1
                    FROM public.character_base character_row
                    WHERE character_row.server_id IS NOT NULL
                      AND character_row.server_id <> 1
                ) THEN
                    RAISE EXCEPTION
                        'Non-Tempest character realms require the realm-scoped lifecycle contract first.'
                        USING ERRCODE = '23514';
                END IF;

                IF NOT EXISTS (
                    SELECT 1
                    FROM pg_constraint constraint_row
                    JOIN pg_attribute source_column
                      ON source_column.attrelid =
                            constraint_row.conrelid
                     AND source_column.attnum =
                            ANY(constraint_row.conkey)
                    JOIN pg_attribute target_column
                      ON target_column.attrelid =
                            constraint_row.confrelid
                     AND target_column.attnum =
                            ANY(constraint_row.confkey)
                    WHERE constraint_row.conrelid =
                            'public.character_base'::regclass
                      AND constraint_row.confrelid =
                            'public.server'::regclass
                      AND constraint_row.contype = 'f'
                      AND constraint_row.convalidated
                      AND source_column.attname = 'server_id'
                      AND target_column.attname = 'id'
                ) THEN
                    RAISE EXCEPTION
                        'character_base.server_id must retain its validated realm foreign key.'
                        USING ERRCODE = '23503';
                END IF;
            END
            $tempest_realm_preflight$;

            UPDATE public.character_base
            SET server_id = 1
            WHERE server_id IS NULL;

            ALTER TABLE public.character_base
                ALTER COLUMN server_id SET DEFAULT 1,
                ALTER COLUMN server_id SET NOT NULL,
                ADD CONSTRAINT ck_character_base_tempest_realm
                    CHECK (server_id = 1);

            CREATE INDEX IF NOT EXISTS ix_character_base_server
                ON public.character_base (server_id);

            COMMENT ON TABLE public.server IS
                'Legacy-named authoritative logical realm catalog.';
            COMMENT ON COLUMN public.character_base.server_id IS
                'Authoritative home realm id; references public.server.';
            """);
}
