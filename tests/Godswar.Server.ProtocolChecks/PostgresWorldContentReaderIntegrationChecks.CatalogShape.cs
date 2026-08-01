using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWorldContentReaderIntegrationChecks
{
    private static async Task AssertPublishedCatalogShapeAsync(
        NpgsqlDataSource dataSource,
        IWorldContentReader postgres)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM map_templates),
                (SELECT COUNT(*) FROM npc_spawn_packets),
                (SELECT COUNT(*) FROM monster_spawn_packets),
                (
                    SELECT COUNT(*)
                    FROM monster_spawn_definitions definitions
                    JOIN monster_content_publication publication
                      ON publication.revision = definitions.revision
                    WHERE publication.family = 'monsters'
                );
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "published-catalog shape query returns one row");
        Check.Equal(
            checked((int)reader.GetInt64(0)),
            postgres.Manifest.Maps.EntryCount,
            "PostgreSQL map rows match the pinned map count");
        Check.Equal(
            0L,
            reader.GetInt64(1),
            "disposable source-parity baseline has no captured NPC override");
        Check.Equal(
            1L,
            reader.GetInt64(2),
            "disposable capture corpus has only the research decoy");
        Check.Equal(
            checked((long)MonsterContentBaselineV1.ExpectedEntryCount),
            reader.GetInt64(3),
            "official monster release stores the reviewed baseline");
        Check.Equal(
            MonsterContentBaselineV1.ExpectedEntryCount,
            postgres.Manifest.Monsters.EntryCount,
            "pinned catalog uses every official monster definition");
        Check.Equal(
            81,
            postgres.Gameplay.Maps.Count,
            "published gameplay catalog includes every supported map");
        Check.Equal(
            50,
            postgres.Gameplay.Links.Count,
            "duplicate legacy portal identities are canonicalized");
        Check.Equal(
            19,
            postgres.Gameplay.WorldBosses.Count,
            "published gameplay catalog includes every approved world boss");
        Check.Equal(
            1,
            postgres.Gameplay.PendingWorldBossAreas.Count,
            "unresolved world-boss policy remains explicit");
        Check.Equal(
            SkillTalentSeeds.Skills.Count,
            postgres.Gameplay.SkillCombatDefinitions.Count,
            "published gameplay catalog includes every skill combat definition");
        Check.Equal(
            SkillTalentSeeds.Classes.Count,
            postgres.Gameplay.Classes.Count,
            "published gameplay catalog includes every class definition");
        Check.Equal(
            SkillTalentSeeds.TalentEffects.Count,
            postgres.Gameplay.TalentEffects.Count,
            "published gameplay catalog includes every talent effect");
        Check.Equal(
            SkillTalentSeeds.Talents.Count,
            postgres.Gameplay.Talents.Count,
            "published gameplay catalog includes every talent definition");
        Check.Equal(
            SkillTalentSeeds.SkillBooks.Count,
            postgres.Gameplay.SkillBooks.Count,
            "published gameplay catalog includes every skill book");
    }
}
