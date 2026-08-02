namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateFighterLevelSeal() =>
        new(
            "20260801_047_fighter_level_seal",
            "Add the durable fighter-level seal at the level 89 cap",
            """
            ALTER TABLE public.character_base
                ADD COLUMN fighter_level_sealed boolean
                    NOT NULL DEFAULT false;

            ALTER TABLE public.character_base
                ADD CONSTRAINT ck_character_base_fighter_level_seal
                    CHECK (
                        NOT fighter_level_sealed
                        OR fighter_job_lv = 89
                    ) NOT VALID;

            ALTER TABLE public.character_base
                VALIDATE CONSTRAINT
                    ck_character_base_fighter_level_seal;

            COMMENT ON COLUMN
                public.character_base.fighter_level_sealed IS
                'Durable authoritative level seal; a sealed fighter remains level 89.';
            """);
}
