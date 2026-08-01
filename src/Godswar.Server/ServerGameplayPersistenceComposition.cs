using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Progression;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure;

namespace Godswar.Server;

internal sealed record ServerGameplayPersistenceProviders(
    ICharacterSnapshotReader CharacterSnapshots,
    ICharacterRuntimeProjectionReader CharacterRuntime,
    IOwnedPetSnapshotReader OwnedPets,
    IExperienceBoostStateReader ExperienceBoosts,
    IWorldBossAreaControlStore WorldBossAreaControl,
    IWorldBossRespawnReader WorldBossRespawns,
    IZodiacLevelStore ZodiacLevels,
    ICharacterCheckpointStore CharacterCheckpoints);

internal static class ServerGameplayPersistenceComposition
{
    public static ServerGameplayPersistenceProviders Create(
        PostgresApplicationDataRuntime postgresRuntime)
    {
        ArgumentNullException.ThrowIfNull(postgresRuntime);
        var characterSnapshots = postgresRuntime.CharacterSnapshots;
        var measuredCharacterSnapshots =
            new MeasuredCharacterSnapshotReader(characterSnapshots);
        var characterRuntime =
            postgresRuntime.CharacterRuntimeProjections;
        var ownedPets = postgresRuntime.OwnedPetSnapshots;
        var experienceBoosts = postgresRuntime.ExperienceBoosts;
        var worldBossAreaControl = postgresRuntime.WorldBossAreaControl;
        var worldBossRespawns = postgresRuntime.WorldBossRespawns;
        var zodiacLevels = postgresRuntime.ZodiacLevels;
        var characterCheckpoints = postgresRuntime.CharacterCheckpoints;

        return new ServerGameplayPersistenceProviders(
            measuredCharacterSnapshots,
            characterRuntime,
            ownedPets,
            experienceBoosts,
            worldBossAreaControl,
            worldBossRespawns,
            zodiacLevels,
            characterCheckpoints);
    }
}
