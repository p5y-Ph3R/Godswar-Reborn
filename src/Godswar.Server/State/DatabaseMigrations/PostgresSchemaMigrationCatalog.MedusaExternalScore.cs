namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMedusaExternalScore() => new(
        "20260828_119_medusa_external_score",
        "Allow actual external-style Medusa completion scores",
        """
        ALTER TABLE public.medusa_completion_rewards
            DROP CONSTRAINT IF EXISTS
                medusa_completion_rewards_final_score_check;

        DO $medusa_completion_score_guard$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname =
                    'ck_medusa_completion_reward_final_score_nonnegative'
            ) THEN
                ALTER TABLE public.medusa_completion_rewards
                    ADD CONSTRAINT
                        ck_medusa_completion_reward_final_score_nonnegative
                    CHECK (final_score >= 0);
            END IF;
        END
        $medusa_completion_score_guard$;

        DO $medusa_title_score_guard$
        BEGIN
            IF to_regclass(
                    'medusa_admission_foundation.medusa_completion_settlements'
                ) IS NOT NULL THEN
                ALTER TABLE
                    medusa_admission_foundation.medusa_completion_settlements
                    DROP CONSTRAINT IF EXISTS
                        medusa_completion_settlements_final_score_check;
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint
                    WHERE conname =
                        'ck_medusa_title_completion_score_minimum'
                ) THEN
                    ALTER TABLE
                        medusa_admission_foundation.medusa_completion_settlements
                        ADD CONSTRAINT
                            ck_medusa_title_completion_score_minimum
                        CHECK (final_score >= 3000);
                END IF;
            END IF;
        END
        $medusa_title_score_guard$;
        """);
}
