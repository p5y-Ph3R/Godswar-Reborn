using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresNpcDialogueMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_023_npc_content_release";
    private const string PreviousMigrationChecksum =
        "C8575C6257B67B372EA66B0354344FAD228C8582014C96533DF9415FCCCB32D5";
    private const string MigrationId =
        "20260729_024_npc_dialogue_content_release";
    private const string MigrationChecksum =
        "CE3D2C012BA7C2E9D7DA9D1D766D9803FE4C76A48F8EF99938C18F5A7664BF9B";

    public static Task RunAsync()
    {
        var catalog = PostgresSchemaMigrationCatalog.All;
        var index = catalog
            .Select((migration, migrationIndex) =>
                (migration, migrationIndex))
            .Single(entry => entry.migration.Id == MigrationId)
            .migrationIndex;
        var migration = catalog[index];

        Check.Equal(
            catalog.Count - 1,
            index,
            "NPC dialogue release is the migration head");
        Check.Equal(
            MigrationChecksum,
            migration.Checksum,
            "NPC dialogue migration checksum is pinned");
        Check.Equal(
            PreviousMigrationId,
            catalog[index - 1].Id,
            "NPC dialogue migration follows NPC spawn authority");
        Check.Equal(
            PreviousMigrationChecksum,
            catalog[index - 1].Checksum,
            "NPC dialogue migration preserves its applied predecessor");

        CheckRevisionManifest(migration.Sql);
        CheckNormalizedCatalog(migration.Sql);
        CheckPublicationPointer(migration.Sql);
        CheckImmutableRows(migration.Sql);
        CheckBoundedInserts(migration.Sql);
        CheckCompletenessAndSpawnCompatibility(migration.Sql);
        CheckNonDestructive(migration.Sql);
        return Task.CompletedTask;
    }

    private static void CheckRevisionManifest(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_dialogue_revisions",
                StringComparison.Ordinal) &&
            sql.Contains(
                "spawn_revision varchar(64) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FOREIGN KEY (spawn_revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.npc_content_revisions (revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (revision ~ '^[0-9A-F]{64}$')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (text_count BETWEEN 1 AND 10000)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (profile_count BETWEEN 1 AND 1024)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (route_count BETWEEN 1 AND 10000)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (menu_entry_count BETWEEN 1 AND 65535)",
                StringComparison.Ordinal),
            "dialogue revisions are bounded and tied to one spawn revision");
    }

    private static void CheckNormalizedCatalog(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_dialogue_texts",
                StringComparison.Ordinal) &&
            sql.Contains(
                "PRIMARY KEY (revision, npc_key)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "octet_length(display_name) <= 1024",
                StringComparison.Ordinal) &&
            sql.Contains(
                "octet_length(description) <= 16384",
                StringComparison.Ordinal),
            "official NPC text is bounded and revision-owned");
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_dialogue_profiles",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (revision, behavior)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (behavior BETWEEN 1 AND 4)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (initial_request_sub_id BETWEEN -1 AND 1000000)",
                StringComparison.Ordinal),
            "dialogue profiles use a closed behavior registry");
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_dialogue_profile_entries",
                StringComparison.Ordinal) &&
            sql.Contains(
                "PRIMARY KEY (revision, profile_key, menu_order)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (revision, profile_key, sub_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (menu_order BETWEEN 0 AND 63)",
                StringComparison.Ordinal),
            "menu entries preserve bounded order and unique actions");
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_dialogue_bindings",
                StringComparison.Ordinal) &&
            Count(
                sql,
                "REFERENCES public.npc_dialogue_profiles (") >= 1 &&
            sql.Contains(
                "REFERENCES public.npc_dialogue_texts (",
                StringComparison.Ordinal) &&
            sql.Contains(
                "client_script_key = npc_key",
                StringComparison.Ordinal) &&
            sql.Contains(
                "client_script_key ~ '^[A-Za-z0-9_]+$'",
                StringComparison.Ordinal),
            "routes reference official text/profile rows and bounded client keys");
    }

    private static void CheckPublicationPointer(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_dialogue_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (family = 'npc-dialogues')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.npc_dialogue_revisions (revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (btrim(publisher) <> '')",
                StringComparison.Ordinal),
            "one constrained pointer selects the official dialogue revision");
    }

    private static void CheckImmutableRows(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE OR REPLACE FUNCTION\n    public.reject_immutable_npc_dialogue_mutation()",
                StringComparison.Ordinal) &&
            Count(
                sql,
                "BEFORE UPDATE OR DELETE ON public.npc_dialogue_") == 5 &&
            Count(
                sql,
                "public.reject_immutable_npc_dialogue_mutation();") == 5,
            "revision, text, profile, menu, and binding rows reject mutation");
        Check.True(
            sql.Contains(
                "BEFORE DELETE ON public.npc_dialogue_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "public.reject_npc_dialogue_publication_delete();",
                StringComparison.Ordinal),
            "the dialogue publication pointer cannot disappear");
    }

    private static void CheckBoundedInserts(string sql)
    {
        Check.True(
            sql.Contains(
                "public.guard_npc_dialogue_content_insert()",
                StringComparison.Ordinal) &&
            sql.Contains(
                "WHERE revision = NEW.revision",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stored_count >= expected_count",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FROM public.npc_dialogue_publication",
                StringComparison.Ordinal) &&
            Count(
                sql,
                "EXECUTE FUNCTION public.guard_npc_dialogue_content_insert();") ==
            4,
            "serialized child inserts reject over-count and post-publication rows");
    }

    private static void CheckCompletenessAndSpawnCompatibility(string sql)
    {
        Check.True(
            sql.Contains(
                "public.validate_npc_dialogue_publication()",
                StringComparison.Ordinal) &&
            sql.Contains(
                "stored_texts, stored_profiles, stored_routes, stored_entries",
                StringComparison.Ordinal) &&
            sql.Contains(
                "release.text_count",
                StringComparison.Ordinal) &&
            sql.Contains(
                "release.profile_count",
                StringComparison.Ordinal) &&
            sql.Contains(
                "release.route_count",
                StringComparison.Ordinal) &&
            sql.Contains(
                "release.menu_entry_count",
                StringComparison.Ordinal) &&
            sql.Contains(
                "release.text_count <> spawn_entry_count",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FROM public.npc_content_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision = release.spawn_revision",
                StringComparison.Ordinal) &&
            sql.Contains(
                "LEFT JOIN public.npc_spawn_definitions spawn",
                StringComparison.Ordinal) &&
            sql.Contains(
                "HAVING COUNT(spawn.object_id) <> 1",
                StringComparison.Ordinal) &&
            sql.Contains(
                "MAX(entry.menu_order) <> COUNT(*) - 1",
                StringComparison.Ordinal),
            "publication rejects partial, incompatible, or non-contiguous content");
    }

    private static void CheckNonDestructive(string sql)
    {
        Check.True(
            !sql.Contains(
                "DROP TABLE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "TRUNCATE",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "DELETE FROM",
                StringComparison.OrdinalIgnoreCase) &&
            !sql.Contains(
                "UPDATE npc_text_templates",
                StringComparison.OrdinalIgnoreCase),
            "dialogue migration is additive and preserves legacy research data");
    }

    private static int Count(string value, string fragment)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(
                   fragment,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += fragment.Length;
        }

        return count;
    }
}
