namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateFighterExperienceUInt32() =>
        new(
            "20260801_048_fighter_experience_uint32",
            "Widen authoritative fighter EXP to the stock-client UInt32 range",
            """
            ALTER TABLE public.character_base
                ALTER COLUMN fighter_job_exp TYPE bigint
                    USING fighter_job_exp::bigint;

            ALTER TABLE public.character_base
                ADD CONSTRAINT ck_character_base_fighter_job_exp_uint32
                    CHECK (
                        fighter_job_exp >= 0
                        AND fighter_job_exp <= 4294967295
                    ) NOT VALID;

            ALTER TABLE public.character_base
                VALIDATE CONSTRAINT
                    ck_character_base_fighter_job_exp_uint32;

            COMMENT ON COLUMN public.character_base.fighter_job_exp IS
                'Authoritative fighter EXP encoded to the legacy client as an unsigned 32-bit value (0..4294967295).';
            """);
}
