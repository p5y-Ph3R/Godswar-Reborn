namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateZodiacSkillGridState() => new(
        "20260723_008_zodiac_skill_grid_state",
        "Persist the sixteen native Zodiac skill-training grids",
        """
        CREATE TABLE IF NOT EXISTS character_zodiac_skill_grids (
            user_id integer NOT NULL
                REFERENCES character_base(id)
                ON DELETE CASCADE,
            grid_index smallint NOT NULL
                CHECK (grid_index BETWEEN 0 AND 15),
            level smallint NOT NULL DEFAULT 0
                CHECK (level BETWEEN 0 AND 50),
            selected_skill_id integer NOT NULL DEFAULT -1,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (user_id, grid_index)
        );
        """);
}
