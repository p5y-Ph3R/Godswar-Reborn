namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateProgressionIntervalAuthority() => new(
            "20260731_033_progression_interval_authority",
            "Add replay-safe online progression interval authority",
            """
            UPDATE public.character_experience_modifiers
            SET remaining_online_ticks = 0
            WHERE remaining_online_ticks < 0;

            ALTER TABLE public.character_experience_modifiers
                ADD CONSTRAINT
                    ck_character_experience_modifiers_online_ticks
                    CHECK (
                        remaining_online_ticks IS NULL
                        OR remaining_online_ticks >= 0
                    );

            CREATE TABLE
                public.character_progression_interval_authority (
                    character_id integer PRIMARY KEY,
                    online_session_id uuid NOT NULL,
                    last_interval_sequence bigint NOT NULL,
                    last_interval_end timestamptz NOT NULL,
                    aggregate_revision bigint NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    CONSTRAINT
                        fk_progression_interval_authority_character
                        FOREIGN KEY (character_id)
                        REFERENCES public.character_base (id)
                        ON DELETE CASCADE,
                    CONSTRAINT
                        ck_progression_interval_authority_session
                        CHECK (
                            online_session_id <>
                                '00000000-0000-0000-0000-000000000000'
                                    ::uuid
                        ),
                    CONSTRAINT
                        ck_progression_interval_authority_sequence
                        CHECK (last_interval_sequence > 0),
                    CONSTRAINT
                        ck_progression_interval_authority_revision
                        CHECK (
                            aggregate_revision >=
                                last_interval_sequence
                        ),
                    CONSTRAINT
                        ck_progression_interval_authority_time
                        CHECK (
                            last_interval_end >=
                                '2020-01-01 00:00:00+00'
                                    ::timestamptz
                            AND last_interval_end <
                                '2100-01-01 00:00:00+00'
                                    ::timestamptz
                            AND created_at <= updated_at
                        )
                );

            CREATE INDEX
                ix_progression_interval_authority_updated
                ON public.character_progression_interval_authority (
                    updated_at,
                    character_id
                );
            """);
}
