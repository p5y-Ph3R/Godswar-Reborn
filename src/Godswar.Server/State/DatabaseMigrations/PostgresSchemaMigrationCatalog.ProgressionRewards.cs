namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateProgressionRewardFoundation() => new(
            "20260731_032_progression_reward_foundation",
            "Make monster-death progression rewards durable and replay-safe",
            """
            ALTER TABLE public.character_base
                ADD COLUMN progression_reward_revision bigint
                    NOT NULL DEFAULT 0,
                ADD CONSTRAINT ck_character_progression_reward_revision
                    CHECK (progression_reward_revision >= 0);

            CREATE TABLE public.monster_death_reward_settlements (
                death_event_id uuid PRIMARY KEY,
                runtime_instance_id uuid NOT NULL,
                map_id smallint NOT NULL,
                monster_object_id bigint NOT NULL,
                spawn_generation bigint NOT NULL,
                death_health_revision bigint NOT NULL,
                account_id integer NOT NULL,
                character_id integer NOT NULL,
                request_hash bytea NOT NULL,
                progression_revision bigint NOT NULL,
                command_inbox_id bigint NOT NULL,
                audit_id bigint NOT NULL,
                outbox_event_id uuid NOT NULL,
                committed_at timestamptz NOT NULL DEFAULT now(),
                CONSTRAINT uq_monster_reward_inbox
                    UNIQUE (command_inbox_id),
                CONSTRAINT uq_monster_reward_audit
                    UNIQUE (audit_id),
                CONSTRAINT uq_monster_reward_outbox
                    UNIQUE (outbox_event_id),
                CONSTRAINT fk_monster_reward_account
                    FOREIGN KEY (account_id)
                    REFERENCES public.accounts (id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_monster_reward_inbox
                    FOREIGN KEY (command_inbox_id)
                    REFERENCES public.command_inbox (id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_monster_reward_audit
                    FOREIGN KEY (audit_id)
                    REFERENCES public.command_audit (id)
                    ON DELETE RESTRICT,
                CONSTRAINT fk_monster_reward_outbox
                    FOREIGN KEY (outbox_event_id)
                    REFERENCES public.outbox_events (event_id)
                    ON DELETE RESTRICT,
                CONSTRAINT ck_monster_reward_runtime
                    CHECK (
                        runtime_instance_id <>
                            '00000000-0000-0000-0000-000000000000'::uuid
                    ),
                CONSTRAINT ck_monster_reward_map
                    CHECK (map_id BETWEEN 0 AND 255),
                CONSTRAINT ck_monster_reward_object
                    CHECK (
                        monster_object_id BETWEEN 1 AND 4294967295
                    ),
                CONSTRAINT ck_monster_reward_generation
                    CHECK (
                        spawn_generation BETWEEN 1 AND 4294967295
                    ),
                CONSTRAINT ck_monster_reward_health_revision
                    CHECK (death_health_revision > 0),
                CONSTRAINT ck_monster_reward_owner
                    CHECK (account_id > 0 AND character_id > 0),
                CONSTRAINT ck_monster_reward_request_hash
                    CHECK (octet_length(request_hash) = 32),
                CONSTRAINT ck_monster_reward_progression_revision
                    CHECK (progression_revision > 0)
            );

            CREATE INDEX ix_monster_reward_character_committed
                ON public.monster_death_reward_settlements (
                    character_id,
                    committed_at DESC,
                    death_event_id
                );

            CREATE INDEX ix_monster_reward_account_committed
                ON public.monster_death_reward_settlements (
                    account_id,
                    committed_at DESC,
                    death_event_id
                );

            CREATE OR REPLACE FUNCTION
                public.reject_monster_reward_settlement_mutation()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION
                    'monster death reward settlements are immutable'
                    USING ERRCODE = '55000';
            END;
            $$;

            CREATE TRIGGER trg_monster_reward_immutable_rows
            BEFORE UPDATE OR DELETE
            ON public.monster_death_reward_settlements
            FOR EACH ROW
            EXECUTE FUNCTION
                public.reject_monster_reward_settlement_mutation();

            CREATE TRIGGER trg_monster_reward_no_truncate
            BEFORE TRUNCATE
            ON public.monster_death_reward_settlements
            FOR EACH STATEMENT
            EXECUTE FUNCTION
                public.reject_monster_reward_settlement_mutation();
            """);
}
