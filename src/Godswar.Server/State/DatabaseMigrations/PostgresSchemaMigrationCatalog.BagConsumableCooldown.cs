namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateBagConsumableCooldownState() =>
        new(
            "20260813_090_bag_consumable_cooldown_state",
            "Persist authoritative per-character bag-consumable cooldowns",
            """
            CREATE TABLE public.character_bag_consumable_cooldowns (
                character_id integer NOT NULL
                    REFERENCES public.character_base(id)
                    ON DELETE CASCADE,
                cooldown_group integer NOT NULL
                    CHECK (cooldown_group > 0),
                ready_at timestamptz NOT NULL,
                updated_at timestamptz NOT NULL,
                PRIMARY KEY (character_id, cooldown_group),
                CONSTRAINT ck_character_bag_consumable_cooldown_time
                    CHECK (ready_at >= updated_at)
            );

            COMMENT ON TABLE
                public.character_bag_consumable_cooldowns IS
                'Authoritative stock Skill/CoolingTime deadlines for bag consumables; one monotonic row per character and cooldown group.';
            """);
}
