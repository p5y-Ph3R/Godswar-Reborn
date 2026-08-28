namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMedusaRewardPolicy() => new(
        "20260827_117_medusa_reward_policy",
        "Move Medusa completion points, titles, and attributes to PostgreSQL",
        """
        CREATE TABLE IF NOT EXISTS medusa_reward_title_definitions (
            title smallint PRIMARY KEY CHECK (title BETWEEN 1 AND 6),
            semantic_key varchar(48) COLLATE "C" NOT NULL UNIQUE,
            display_name varchar(80) NOT NULL
                CHECK (length(btrim(display_name)) > 0),
            client_title_id integer NOT NULL UNIQUE
                CHECK (client_title_id > 0),
            physical_attack_basis_points smallint NOT NULL
                CHECK (physical_attack_basis_points BETWEEN 1 AND 10000),
            magic_attack_basis_points smallint NOT NULL
                CHECK (magic_attack_basis_points BETWEEN 1 AND 10000),
            physical_defense_basis_points smallint NOT NULL
                CHECK (physical_defense_basis_points BETWEEN 1 AND 10000),
            magic_defense_basis_points smallint NOT NULL
                CHECK (magic_defense_basis_points BETWEEN 1 AND 10000),
            updated_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (title, client_title_id)
        );

        INSERT INTO medusa_reward_title_definitions (
            title, semantic_key, display_name, client_title_id,
            physical_attack_basis_points, magic_attack_basis_points,
            physical_defense_basis_points, magic_defense_basis_points)
        VALUES
            (1, 'medusa.challengers', 'Medusa Challengers', 5011,
                300, 300, 300, 300),
            (2, 'medusa.slayers', 'Medusa Slayers', 5010,
                200, 200, 200, 200),
            (3, 'medusa.executioners', 'Medusa Executioners', 5009,
                100, 100, 100, 100),
            (4, 'medusa.gorgon-breaker', 'Gorgon Breaker', 5154,
                400, 400, 400, 400),
            (5, 'medusa.bane-of-the-three-sisters',
                'Bane of the Three Sisters', 5153,
                500, 500, 500, 500),
            (6, 'medusa.heir-of-perseus', 'Heir of Perseus', 5152,
                600, 600, 600, 600)
        ON CONFLICT (title) DO NOTHING;

        CREATE TABLE IF NOT EXISTS medusa_completion_reward_rules (
            difficulty smallint NOT NULL
                CHECK (difficulty BETWEEN 1 AND 3),
            reward_kind smallint NOT NULL
                CHECK (reward_kind IN (1, 2)),
            threshold integer NOT NULL,
            honor_points integer NOT NULL CHECK (honor_points > 0),
            title smallint NULL REFERENCES
                medusa_reward_title_definitions(title),
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (difficulty, reward_kind, threshold),
            CONSTRAINT ck_medusa_completion_reward_rule_shape CHECK (
                (reward_kind = 1 AND threshold BETWEEN 0 AND 2999
                    AND title IS NULL) OR
                (reward_kind = 2 AND threshold BETWEEN 1 AND 2400))
        );

        -- reward_kind 1: minimum incomplete score.
        INSERT INTO medusa_completion_reward_rules (
            difficulty, reward_kind, threshold, honor_points, title)
        VALUES
            (1, 1,    0,  300, NULL),
            (1, 1,  950,  375, NULL),
            (1, 1, 1200,  450, NULL),
            (1, 1, 1500,  525, NULL),
            (1, 1, 1700,  600, NULL),
            (1, 1, 1900,  675, NULL),
            (1, 1, 2200,  750, NULL),
            (2, 1,    0,  300, NULL),
            (2, 1,  950,  600, NULL),
            (2, 1, 1200,  750, NULL),
            (2, 1, 1500,  900, NULL),
            (2, 1, 1700, 1050, NULL),
            (2, 1, 1900, 1200, NULL),
            (2, 1, 2200, 1350, NULL),
            (3, 1,    0,  450, NULL),
            (3, 1,  950,  900, NULL),
            (3, 1, 1200, 1125, NULL),
            (3, 1, 1500, 1350, NULL),
            (3, 1, 1700, 1575, NULL),
            (3, 1, 1900, 1800, NULL),
            (3, 1, 2200, 2025, NULL)
        ON CONFLICT (difficulty, reward_kind, threshold) DO NOTHING;

        -- reward_kind 2: inclusive maximum completion time in seconds.
        INSERT INTO medusa_completion_reward_rules (
            difficulty, reward_kind, threshold, honor_points, title)
        VALUES
            (1, 2,  600, 1350, NULL),
            (1, 2,  900, 1275, NULL),
            (1, 2, 1200, 1200, NULL),
            (1, 2, 1500, 1125, NULL),
            (1, 2, 1800, 1050, NULL),
            (1, 2, 2400,  975, NULL),
            (2, 2,  600, 2250, 1),
            (2, 2,  900, 2175, 2),
            (2, 2, 1200, 2100, 3),
            (2, 2, 1500, 2025, NULL),
            (2, 2, 1800, 1950, NULL),
            (2, 2, 2400, 1800, NULL),
            (3, 2,  600, 3375, 6),
            (3, 2,  900, 3300, 5),
            (3, 2, 1200, 3150, 4),
            (3, 2, 1500, 3075, NULL),
            (3, 2, 1800, 2925, NULL),
            (3, 2, 2400, 2700, NULL)
        ON CONFLICT (difficulty, reward_kind, threshold) DO NOTHING;

        ALTER TABLE medusa_completion_rewards
            ADD CONSTRAINT fk_medusa_completion_reward_title_definition
            FOREIGN KEY (title)
            REFERENCES medusa_reward_title_definitions(title);

        ALTER TABLE character_title_ownership
            DROP CONSTRAINT IF EXISTS ck_character_title_client_mapping;
        ALTER TABLE character_title_ownership
            ADD CONSTRAINT fk_character_title_reward_definition
            FOREIGN KEY (title, title_id)
            REFERENCES medusa_reward_title_definitions(
                title, client_title_id);

        ALTER TABLE medusa_completion_reward_members
            DROP CONSTRAINT IF EXISTS ck_medusa_member_awarded_title_id;
        ALTER TABLE medusa_completion_reward_members
            ADD CONSTRAINT ck_medusa_member_awarded_title_nonnegative
            CHECK (awarded_title_id >= 0);
        """);
}
