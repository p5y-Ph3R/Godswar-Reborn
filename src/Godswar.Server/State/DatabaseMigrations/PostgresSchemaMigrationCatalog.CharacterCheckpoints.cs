namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateCharacterCheckpointVersions() => new(
            "20260730_030_character_checkpoint_versions",
            "Add fenced position and vitals checkpoint versions",
            """
            ALTER TABLE public.character_base
                ADD COLUMN position_revision bigint NOT NULL DEFAULT 0,
                ADD COLUMN checkpoint_owner_id uuid,
                ADD COLUMN checkpoint_owner_generation bigint
                    NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_character_base_position_revision
                    CHECK (position_revision >= 0),
                ADD CONSTRAINT ck_character_base_vitals_revision
                    CHECK (vitals_revision >= 0),
                ADD CONSTRAINT
                    ck_character_base_checkpoint_owner_generation
                    CHECK (checkpoint_owner_generation >= 0),
                ADD CONSTRAINT ck_character_base_checkpoint_owner_pair
                    CHECK (
                        checkpoint_owner_id IS NULL
                        OR (
                            checkpoint_owner_generation > 0
                            AND checkpoint_owner_id <>
                                '00000000-0000-0000-0000-000000000000'
                                    ::uuid
                        )
                    );
            """);
}
