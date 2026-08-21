namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateRealmScopedWorldBossControl() => new(
            "20260820_095_realm_scoped_world_boss_control",
            "Scope mutable world-boss area control by logical realm",
            """
            ALTER TABLE public.faction_area_experience_control
                ADD COLUMN realm_id integer;

            UPDATE public.faction_area_experience_control
            SET realm_id = 1;

            ALTER TABLE public.faction_area_experience_control
                ALTER COLUMN realm_id SET NOT NULL,
                ADD CONSTRAINT fk_faction_area_control_realm
                    FOREIGN KEY (realm_id)
                    REFERENCES public.server(id)
                    ON DELETE RESTRICT;

            ALTER TABLE public.faction_area_experience_control
                DROP CONSTRAINT faction_area_experience_control_pkey,
                ADD CONSTRAINT pk_faction_area_experience_control
                    PRIMARY KEY (realm_id, map_id);

            DROP INDEX IF EXISTS
                public.ix_faction_area_experience_control_active;
            CREATE INDEX ix_faction_area_experience_control_active
                ON public.faction_area_experience_control (
                    realm_id,
                    map_id,
                    controlling_camp,
                    expires_at
                );

            COMMENT ON COLUMN
                public.faction_area_experience_control.realm_id IS
                'Logical realm owning this mutable world-boss control row.';
            """);
}
