using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaRunRuntime
{
    private static int[] ValidateAndCopyCharacters(
        IReadOnlyCollection<int> admittedCharacterIds)
    {
        if (admittedCharacterIds.Count < MedusaIslandPolicy.MinimumPartySize ||
            admittedCharacterIds.Count > MedusaIslandPolicy.MaximumPartySize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(admittedCharacterIds),
                "A Medusa run must fix between one and five participants.");
        }

        var characters = admittedCharacterIds.ToArray();
        if (characters.Any(characterId => characterId <= 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(admittedCharacterIds),
                "Admitted character IDs must be positive.");
        }
        if (characters.Distinct().Count() != characters.Length)
        {
            throw new ArgumentException(
                "Admitted character IDs must be unique.",
                nameof(admittedCharacterIds));
        }

        Array.Sort(characters);
        return characters;
    }

    private static SpawnState[] ValidateAndCopySpawns(
        IReadOnlyCollection<MedusaRunSpawnDefinition> spawns,
        MedusaEncounterDifficultyDefinition difficulty)
    {
        var expectedCount = MedusaIslandEncounterPolicy.TotalEnemyCount(
            difficulty);
        if (spawns.Count != expectedCount)
        {
            throw new ArgumentException(
                $"A Medusa run requires exactly {expectedCount} spawns.",
                nameof(spawns));
        }

        var definitions = spawns.ToArray();
        if (definitions.Any(spawn =>
                string.IsNullOrWhiteSpace(spawn.RosterSpawnId) ||
                string.IsNullOrWhiteSpace(spawn.TemplateKey) ||
                spawn.ObjectId == 0 ||
                spawn.SpawnGeneration == 0))
        {
            throw new ArgumentException(
                "Roster IDs, object IDs, and spawn generations are required.",
                nameof(spawns));
        }
        if (definitions.Select(spawn => spawn.RosterSpawnId)
                .Distinct(StringComparer.Ordinal)
                .Count() != definitions.Length ||
            definitions.Select(spawn => spawn.ObjectId).Distinct().Count() !=
                definitions.Length ||
            definitions.Select(spawn =>
                    (spawn.ObjectId, spawn.SpawnGeneration))
                .Distinct()
                .Count() != definitions.Length)
        {
            throw new ArgumentException(
                "Every roster, object, and generation identity must be unique.",
                nameof(spawns));
        }

        foreach (var definition in definitions)
        {
            if (!MedusaIslandRosterPolicy.TryGetSpawn(
                    definition.RosterSpawnId,
                    out var authoredSpawn) ||
                authoredSpawn.EncounterRole != definition.Role ||
                authoredSpawn.Rank != definition.Rank ||
                !MedusaIslandRosterPolicy.TryResolveTemplate(
                    difficulty.Difficulty,
                    authoredSpawn.TemplateAlias,
                    out var authoredTemplate) ||
                !string.Equals(
                    authoredTemplate.TemplateKey,
                    definition.TemplateKey,
                    StringComparison.Ordinal) ||
                authoredTemplate.Rank != authoredSpawn.Rank ||
                authoredTemplate.MapId != difficulty.ContentMapId.Value)
            {
                throw new ArgumentException(
                    $"Spawn binding {definition.RosterSpawnId} must match " +
                    "its authored difficulty template, role, and rank.",
                    nameof(spawns));
            }
        }

        foreach (var expected in difficulty.Enemies)
        {
            var matching = definitions.Where(spawn =>
                spawn.Role == expected.Role).ToArray();
            if (matching.Length != expected.Count ||
                matching.Any(spawn => spawn.Rank != expected.Rank))
            {
                throw new ArgumentException(
                    $"Spawn role {expected.Role} must contain exactly " +
                    $"{expected.Count} {expected.Rank} entries.",
                    nameof(spawns));
            }
        }

        var knownRoles = difficulty.Enemies.Select(enemy => enemy.Role)
            .ToHashSet();
        if (definitions.Any(spawn => !knownRoles.Contains(spawn.Role)))
        {
            throw new ArgumentException(
                "The run contains an unknown encounter role.",
                nameof(spawns));
        }

        var states = definitions.Select(definition =>
            {
                var authored = MedusaIslandRosterPolicy.Spawns.Single(
                    spawn => string.Equals(
                        spawn.SpawnId,
                        definition.RosterSpawnId,
                        StringComparison.Ordinal));
                if (!MedusaMonsterContentCatalog.Current.TryGetMonster(
                        difficulty.Difficulty,
                        authored.TemplateAlias,
                        out var rule))
                {
                    throw new ArgumentException(
                        $"Spawn {definition.RosterSpawnId} has no score rule.",
                        nameof(spawns));
                }
                return new SpawnState(definition, rule.Score);
            })
            .OrderBy(spawn => spawn.Definition.ObjectId)
            .ToArray();
        var fixedScore = states.Sum(spawn => spawn.ScoreValue);
        if (fixedScore !=
            MedusaIslandEncounterPolicy.TotalVictoryScore(difficulty))
        {
            throw new ArgumentException(
                "The Medusa run score must match its authored roster.",
                nameof(spawns));
        }

        return states;
    }
}
