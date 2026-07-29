using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresNpcContentMigrationChecks
{
    private const string PreviousMigrationId =
        "20260729_022_pet_level_progression";
    private const string PreviousMigrationChecksum =
        "86C581294D06B00E64AA8C7F84C79019521BCA2E3B860B09FBA77942E5BD288D";
    private const string MigrationId =
        "20260729_023_npc_content_release";
    private const string MigrationChecksum =
        "C8575C6257B67B372EA66B0354344FAD228C8582014C96533DF9415FCCCB32D5";
    private const string NextMigrationId =
        "20260729_024_npc_dialogue_content_release";

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
            MigrationChecksum,
            migration.Checksum,
            "NPC content migration checksum is pinned");
        Check.Equal(
            PreviousMigrationId,
            catalog[index - 1].Id,
            "NPC content migration follows pet level progression");
        Check.Equal(
            PreviousMigrationChecksum,
            catalog[index - 1].Checksum,
            "NPC content migration preserves its applied predecessor");
        Check.Equal(
            NextMigrationId,
            catalog[index + 1].Id,
            "NPC content migration has the expected forward-only successor");

        CheckRevisionCatalog(migration.Sql);
        CheckSpawnCatalog(migration.Sql);
        CheckPublicationPointer(migration.Sql);
        CheckImmutabilityTriggers(migration.Sql);
        CheckBoundedDefinitionInsert(migration.Sql);
        CheckPublicationCompleteness(migration.Sql);
        CheckNonDestructiveMigration(migration.Sql);
        return Task.CompletedTask;
    }

    private static void CheckRevisionCatalog(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_content_revisions",
                StringComparison.Ordinal) &&
            sql.Contains(
                "revision varchar(64) PRIMARY KEY",
                StringComparison.Ordinal) &&
            sql.Contains(
                "entry_count integer NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "source varchar(64) NOT NULL",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (revision ~ '^[0-9A-F]{64}$')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (entry_count BETWEEN 0 AND 10000)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (btrim(source) <> '')",
                StringComparison.Ordinal),
            "NPC revisions have a bounded canonical identity and manifest");
    }

    private static void CheckSpawnCatalog(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_spawn_definitions",
                StringComparison.Ordinal) &&
            sql.Contains(
                "PRIMARY KEY (revision, map_id, object_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "UNIQUE (revision, map_id, interaction_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FOREIGN KEY (revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.npc_content_revisions (revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FOREIGN KEY (map_id)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.map_templates (map_id)",
                StringComparison.Ordinal) &&
            Count(sql, "ON DELETE RESTRICT") >= 3,
            "NPC rows have revision/map ownership and collision guards");

        Check.True(
            sql.Contains(
                "CHECK (object_id BETWEEN 1 AND 4294967295)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (interaction_id BETWEEN 1 AND 4294967295)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (appearance_type BETWEEN 1 AND 4294967295)",
                StringComparison.Ordinal) &&
            Count(sql, "NOT IN (\n") >= 3 &&
            sql.Contains(
                "CHECK (octet_length(detail_10077) <= 65535)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (octet_length(detail_10080) <= 65535)",
                StringComparison.Ordinal),
            "NPC packet fields reject invalid numeric and oversized values");

        Check.True(
            sql.Contains(
                "CREATE INDEX ix_npc_spawn_definitions_canonical",
                StringComparison.Ordinal) &&
            HasOrderedFragments(
                sql[
                    sql.IndexOf(
                        "CREATE INDEX ix_npc_spawn_definitions_canonical",
                        StringComparison.Ordinal)..],
                "revision",
                "map_id",
                "npc_key",
                "template_key",
                "object_id"),
            "NPC rows have a deterministic revision/map read index");
    }

    private static void CheckPublicationPointer(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE TABLE public.npc_content_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "family varchar(16) PRIMARY KEY",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (family = 'npcs')",
                StringComparison.Ordinal) &&
            sql.Contains(
                "FOREIGN KEY (revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "REFERENCES public.npc_content_revisions (revision)",
                StringComparison.Ordinal) &&
            sql.Contains(
                "CHECK (btrim(publisher) <> '')",
                StringComparison.Ordinal),
            "one constrained publication pointer selects an existing revision");
    }

    private static void CheckImmutabilityTriggers(string sql)
    {
        Check.True(
            sql.Contains(
                "CREATE OR REPLACE FUNCTION public.reject_immutable_npc_content_mutation()",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE UPDATE OR DELETE ON public.npc_content_revisions",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE UPDATE OR DELETE ON public.npc_spawn_definitions",
                StringComparison.Ordinal) &&
            Count(
                sql,
                "EXECUTE FUNCTION public.reject_immutable_npc_content_mutation()") ==
            2,
            "revision manifests and NPC rows reject mutation");

        Check.True(
            sql.Contains(
                "CREATE OR REPLACE FUNCTION public.reject_npc_content_publication_delete()",
                StringComparison.Ordinal) &&
            sql.Contains(
                "BEFORE DELETE ON public.npc_content_publication",
                StringComparison.Ordinal) &&
            sql.Contains(
                "EXECUTE FUNCTION public.reject_npc_content_publication_delete()",
                StringComparison.Ordinal),
            "the publication pointer can move but cannot disappear");
    }

    private static void CheckBoundedDefinitionInsert(string sql)
    {
        Check.True(
            HasOrderedFragments(
                sql,
                "CREATE OR REPLACE FUNCTION public.guard_npc_content_definition_insert()",
                "SELECT entry_count",
                "INTO STRICT declared_entry_count",
                "WHERE revision = NEW.revision",
                "FOR UPDATE;",
                "SELECT COUNT(*)::integer",
                "INTO stored_entry_count",
                "IF stored_entry_count >= declared_entry_count THEN",
                "RETURN NEW;",
                "END;",
                "CREATE TRIGGER trg_npc_spawn_definitions_bounded_insert",
                "BEFORE INSERT ON public.npc_spawn_definitions",
                "EXECUTE FUNCTION public.guard_npc_content_definition_insert()") &&
            Count(sql, "FOR UPDATE;") == 2,
            "serialized inserts cannot exceed a revision's immutable manifest");
    }

    private static void CheckPublicationCompleteness(string sql)
    {
        Check.True(
            HasOrderedFragments(
                sql,
                "CREATE OR REPLACE FUNCTION public.validate_npc_content_publication()",
                "SELECT entry_count",
                "INTO STRICT declared_entry_count",
                "WHERE revision = NEW.revision",
                "FOR UPDATE;",
                "SELECT COUNT(*)::integer",
                "INTO stored_entry_count",
                "IF stored_entry_count <> declared_entry_count THEN",
                "RETURN NEW;",
                "END;",
                "CREATE TRIGGER trg_npc_content_publication_complete",
                "BEFORE INSERT OR UPDATE ON public.npc_content_publication",
                "EXECUTE FUNCTION public.validate_npc_content_publication()") &&
            Count(sql, "RETURN NEW;") == 2 &&
            Count(sql, "END;") == 4,
            "publication accepts only complete manifests and trigger bodies " +
            "are valid PL/pgSQL");
    }

    private static void CheckNonDestructiveMigration(string sql)
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
                StringComparison.OrdinalIgnoreCase),
            "NPC content migration does not destroy authoritative data");
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

    private static bool HasOrderedFragments(
        string value,
        params string[] fragments)
    {
        var index = 0;
        foreach (var fragment in fragments)
        {
            index = value.IndexOf(
                fragment,
                index,
                StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            index += fragment.Length;
        }

        return true;
    }
}
