namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateMedusaCompletionRewards() => new(
        "20260827_113_medusa_completion_rewards",
        "Persist idempotent Medusa Honor rewards",
        """
        ALTER TABLE character_base
            ADD COLUMN IF NOT EXISTS medusa_honor_points integer
                NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS medusa_reward_revision bigint
                NOT NULL DEFAULT 0;

        DO $medusa_character_guards$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_medusa_honor_nonnegative'
            ) THEN
                ALTER TABLE character_base
                    ADD CONSTRAINT ck_character_medusa_honor_nonnegative
                    CHECK (medusa_honor_points >= 0);
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_medusa_reward_revision'
            ) THEN
                ALTER TABLE character_base
                    ADD CONSTRAINT ck_character_medusa_reward_revision
                    CHECK (medusa_reward_revision >= 0);
            END IF;
        END
        $medusa_character_guards$;

        CREATE TABLE IF NOT EXISTS medusa_completion_rewards (
            world_instance_id uuid PRIMARY KEY,
            realm_id smallint NOT NULL,
            difficulty smallint NOT NULL CHECK (difficulty BETWEEN 1 AND 3),
            completed_at_ticks bigint NOT NULL CHECK (completed_at_ticks > 0),
            elapsed_ticks bigint NOT NULL CHECK (elapsed_ticks >= 0),
            final_score integer NOT NULL CHECK (final_score BETWEEN 0 AND 3000),
            hard_points integer NOT NULL CHECK (hard_points > 0),
            title smallint NULL CHECK (title BETWEEN 1 AND 6),
            character_ids integer[] NOT NULL,
            settled_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT ck_medusa_completion_reward_roster
                CHECK (cardinality(character_ids) BETWEEN 1 AND 5)
        );

        CREATE TABLE IF NOT EXISTS medusa_completion_reward_members (
            world_instance_id uuid NOT NULL REFERENCES
                medusa_completion_rewards(world_instance_id)
                ON DELETE CASCADE,
            character_id integer NOT NULL REFERENCES character_base(id),
            camp smallint NOT NULL CHECK (camp BETWEEN 1 AND 2),
            honor_before integer NOT NULL CHECK (honor_before >= 0),
            honor_after integer NOT NULL CHECK (honor_after >= honor_before),
            reward_revision bigint NOT NULL CHECK (reward_revision > 0),
            PRIMARY KEY (world_instance_id, character_id)
        );
        """);
}
