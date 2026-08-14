namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreatePetGrowthPreview() => new(
        "20260812_079_pet_growth_preview",
        "Persist one session-fenced Phoenix Growth preview per character",
        """
        CREATE TABLE public.character_pet_growth_previews (
            user_id integer PRIMARY KEY
                REFERENCES public.character_base(id) ON DELETE CASCADE,
            pet_id bigint NOT NULL
                REFERENCES public.character_pets(id) ON DELETE CASCADE,
            preview_operation_id uuid NOT NULL UNIQUE,
            connection_id uuid NOT NULL,
            owner_id uuid NOT NULL,
            owner_generation bigint NOT NULL CHECK (owner_generation > 0),
            expected_pet_level smallint NOT NULL
                CHECK (expected_pet_level BETWEEN 1 AND 120),
            expected_pet_revision bigint NOT NULL
                CHECK (expected_pet_revision >= 0),
            expected_stat_revisions bigint[] NOT NULL
                CHECK (
                    cardinality(expected_stat_revisions) = 6 AND
                    0 <= ALL(expected_stat_revisions)
                ),
            growth_rates numeric(18,6)[] NOT NULL
                CHECK (
                    cardinality(growth_rates) = 6 AND
                    0 < ALL(growth_rates)
                ),
            created_at timestamptz NOT NULL DEFAULT transaction_timestamp(),
            expires_at timestamptz NOT NULL,
            CHECK (expires_at > created_at)
        );

        CREATE INDEX ix_character_pet_growth_previews_expiry
            ON public.character_pet_growth_previews(expires_at);
        """);
}
