using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPublishedWorldContentMigrationChecks
{
    public static void Run()
    {
        var monster = PostgresSchemaMigrationCatalog.All.Single(
            static migration =>
                migration.Id == "20260801_036_monster_content_release");
        var bootstrap = PostgresSchemaMigrationCatalog.All.Single(
            static migration =>
                migration.Id ==
                "20260801_037_enter_bootstrap_content_release");
        var gameplay = PostgresSchemaMigrationCatalog.All.Single(
            static migration =>
                migration.Id ==
                "20260801_039_gameplay_content_release");

        AssertContains(
            monster.Sql,
            "CREATE TABLE public.monster_content_revisions",
            "CREATE TABLE public.monster_spawn_definitions",
            "CREATE TABLE public.monster_content_publication",
            "CHECK (entry_count BETWEEN 0 AND 100000)",
            "CHECK (octet_length(clear_bytes) BETWEEN 108 AND 1200)",
            "trg_monster_content_revisions_immutable",
            "trg_monster_spawn_definitions_immutable",
            "trg_monster_content_publication_complete",
            "trg_monster_content_publication_no_delete");
        Check.True(
            !monster.Sql.Contains(
                "monster_spawn_packets",
                StringComparison.OrdinalIgnoreCase),
            "official monster migration does not depend on the capture table");

        AssertContains(
            bootstrap.Sql,
            "CREATE TABLE public.enter_bootstrap_revisions",
            "CREATE TABLE public.enter_bootstrap_packets",
            "CREATE TABLE public.enter_bootstrap_publication",
            "CHECK (packet_count BETWEEN 0 AND 256)",
            "CHECK (total_bytes BETWEEN 0 AND 262144)",
            "trg_enter_bootstrap_revisions_immutable",
            "trg_enter_bootstrap_packets_immutable",
            "trg_enter_bootstrap_publication_complete",
            "trg_enter_bootstrap_publication_no_delete");
        Check.True(
            !bootstrap.Sql.Contains(
                "server_packet_templates",
                StringComparison.OrdinalIgnoreCase) &&
            !bootstrap.Sql.Contains(
                "packet_transactions",
                StringComparison.OrdinalIgnoreCase),
            "official bootstrap migration does not depend on research data");

        AssertContains(
            gameplay.Sql,
            "CREATE TABLE public.gameplay_content_revisions",
            "CREATE TABLE public.gameplay_map_definitions",
            "CREATE TABLE public.gameplay_map_address_points",
            "CREATE TABLE public.gameplay_map_links",
            "CREATE TABLE public.gameplay_monster_templates",
            "CREATE TABLE public.gameplay_world_boss_definitions",
            "CREATE TABLE public.gameplay_pending_world_boss_areas",
            "CREATE TABLE public.gameplay_skill_combat_definitions",
            "CREATE TABLE public.gameplay_content_publication",
            "trg_gameplay_revisions_immutable",
            "trg_gameplay_maps_bounded_insert",
            "trg_gameplay_monsters_bounded_insert",
            "trg_gameplay_skills_bounded_insert",
            "trg_gameplay_publication_complete",
            "trg_gameplay_publication_no_delete");
        Check.True(
            !gameplay.Sql.Contains(
                "monster_spawn_packets",
                StringComparison.OrdinalIgnoreCase) &&
            !gameplay.Sql.Contains(
                "server_packet_templates",
                StringComparison.OrdinalIgnoreCase) &&
            !gameplay.Sql.Contains(
                "packet_transactions",
                StringComparison.OrdinalIgnoreCase),
            "official gameplay publication is capture-corpus independent");
    }

    private static void AssertContains(
        string sql,
        params string[] fragments)
    {
        foreach (var fragment in fragments)
        {
            Check.True(
                sql.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                $"published world-content migration contains {fragment}");
        }
    }
}
