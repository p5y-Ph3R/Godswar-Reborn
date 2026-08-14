namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration CreateMonsterCombatAuthority() =>
        new(
            "20260814_093_monster_combat_authority",
            "Retain authored monster attack type in sealed gameplay content",
            """
            ALTER TABLE public.gameplay_monster_templates
                ADD COLUMN attack_type smallint;

            ALTER TABLE public.gameplay_monster_templates
                ADD CONSTRAINT ck_gameplay_monsters_attack_type CHECK (
                    attack_type IS NULL OR attack_type IN (1, 2, 3)
                );

            ALTER TABLE public.gameplay_map_definitions
                ADD COLUMN map_mode smallint;

            ALTER TABLE public.gameplay_map_definitions
                ADD CONSTRAINT ck_gameplay_maps_mode CHECK (
                    map_mode IS NULL OR map_mode BETWEEN 0 AND 5
                );
            """);
}
