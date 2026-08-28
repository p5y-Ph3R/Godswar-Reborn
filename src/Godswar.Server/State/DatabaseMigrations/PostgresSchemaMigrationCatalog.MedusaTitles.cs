namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMedusaTitleOwnership() => new(
        "20260827_114_medusa_title_ownership",
        "Persist and project earned Medusa titles",
        """
        ALTER TABLE character_base
            ADD COLUMN IF NOT EXISTS selected_title_id integer
                NOT NULL DEFAULT 0;

        DO $selected_title_guard$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_character_selected_title_nonnegative'
            ) THEN
                ALTER TABLE character_base
                    ADD CONSTRAINT ck_character_selected_title_nonnegative
                    CHECK (selected_title_id >= 0);
            END IF;
        END
        $selected_title_guard$;

        ALTER TABLE medusa_completion_rewards
            DROP CONSTRAINT IF EXISTS
                medusa_completion_rewards_hard_points_check;

        DO $medusa_reward_guards$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname =
                    'ck_medusa_completion_reward_hard_points_nonnegative'
            ) THEN
                ALTER TABLE medusa_completion_rewards
                    ADD CONSTRAINT
                        ck_medusa_completion_reward_hard_points_nonnegative
                    CHECK (hard_points >= 0);
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'uq_medusa_completion_reward_title'
            ) THEN
                ALTER TABLE medusa_completion_rewards
                    ADD CONSTRAINT uq_medusa_completion_reward_title
                    UNIQUE (world_instance_id, title);
            END IF;
        END
        $medusa_reward_guards$;

        ALTER TABLE medusa_completion_reward_members
            DROP CONSTRAINT IF EXISTS
                medusa_completion_reward_members_camp_check;

        ALTER TABLE medusa_completion_reward_members
            ADD COLUMN IF NOT EXISTS awarded_title_id integer
                NOT NULL DEFAULT 0;

        DO $medusa_member_title_guard$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_medusa_reward_member_camp'
            ) THEN
                ALTER TABLE medusa_completion_reward_members
                    ADD CONSTRAINT ck_medusa_reward_member_camp
                    CHECK (camp IN (0, 1));
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'ck_medusa_member_awarded_title_id'
            ) THEN
                ALTER TABLE medusa_completion_reward_members
                    ADD CONSTRAINT ck_medusa_member_awarded_title_id
                    CHECK (awarded_title_id IN (
                        0, 5009, 5010, 5011, 5152, 5153, 5154));
            END IF;
        END
        $medusa_member_title_guard$;

        UPDATE medusa_completion_reward_members member
        SET awarded_title_id = CASE reward.title
            WHEN 1 THEN 5011
            WHEN 2 THEN 5010
            WHEN 3 THEN 5009
            WHEN 4 THEN 5154
            WHEN 5 THEN 5153
            WHEN 6 THEN 5152
            ELSE 0
        END
        FROM medusa_completion_rewards reward
        WHERE reward.world_instance_id = member.world_instance_id
          AND reward.title IS NOT NULL;

        CREATE TABLE IF NOT EXISTS character_title_ownership (
            character_id integer NOT NULL REFERENCES character_base(id),
            title smallint NOT NULL CHECK (title BETWEEN 1 AND 6),
            title_id integer NOT NULL,
            source_world_instance_id uuid NOT NULL,
            acquired_at timestamptz NOT NULL,
            PRIMARY KEY (character_id, title),
            UNIQUE (source_world_instance_id, character_id),
            CONSTRAINT ck_character_title_client_mapping CHECK (
                (title = 1 AND title_id = 5011) OR
                (title = 2 AND title_id = 5010) OR
                (title = 3 AND title_id = 5009) OR
                (title = 4 AND title_id = 5154) OR
                (title = 5 AND title_id = 5153) OR
                (title = 6 AND title_id = 5152)),
            CONSTRAINT fk_character_title_medusa_reward
                FOREIGN KEY (source_world_instance_id, title)
                REFERENCES medusa_completion_rewards(
                    world_instance_id, title)
        );

        INSERT INTO character_title_ownership (
            character_id, title, title_id,
            source_world_instance_id, acquired_at)
        SELECT DISTINCT ON (member.character_id, reward.title)
            member.character_id,
            reward.title,
            member.awarded_title_id,
            reward.world_instance_id,
            reward.settled_at
        FROM medusa_completion_reward_members member
        JOIN medusa_completion_rewards reward
          ON reward.world_instance_id = member.world_instance_id
        WHERE reward.title IS NOT NULL
        ORDER BY
            member.character_id,
            reward.title,
            reward.settled_at,
            reward.world_instance_id
        ON CONFLICT (character_id, title) DO NOTHING;

        WITH latest_title AS (
            SELECT DISTINCT ON (member.character_id)
                member.character_id,
                member.awarded_title_id
            FROM medusa_completion_reward_members member
            JOIN medusa_completion_rewards reward
              ON reward.world_instance_id = member.world_instance_id
            WHERE member.awarded_title_id > 0
            ORDER BY
                member.character_id,
                reward.settled_at DESC,
                reward.world_instance_id DESC
        )
        UPDATE character_base character
        SET selected_title_id = latest_title.awarded_title_id
        FROM latest_title
        WHERE character.id = latest_title.character_id
          AND character.selected_title_id = 0;
        """);
}
