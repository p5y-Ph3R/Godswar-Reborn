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
    IZodiacLevelStore ZodiacLevels);

internal static class ServerGameplayPersistenceComposition
{
    public static ServerGameplayPersistenceProviders Create(
        PostgresApplicationDataRuntime? postgresRuntime,
        ICharacterRuntimeProjectionReader? localProvider)
    {
        var characterSnapshots =
            postgresRuntime?.CharacterSnapshots ??
            localProvider as ICharacterSnapshotReader ??
            throw Missing("character snapshot reader");
        var measuredCharacterSnapshots =
            new MeasuredCharacterSnapshotReader(
                characterSnapshots,
                postgresRuntime is null
                    ? CharacterSnapshotProvider.Json
                    : CharacterSnapshotProvider.PostgreSql);
        var characterRuntime =
            postgresRuntime?.CharacterRuntimeProjections ??
            localProvider ??
            throw Missing("character runtime projection reader");
        var ownedPets = postgresRuntime?.OwnedPetSnapshots ??
            localProvider as IOwnedPetSnapshotReader ??
            throw Missing("owned-pet snapshot reader");
        var experienceBoosts = postgresRuntime?.ExperienceBoosts ??
            localProvider as IExperienceBoostStateReader ??
            throw Missing("experience-boost state reader");
        var worldBossAreaControl =
            postgresRuntime?.WorldBossAreaControl ??
            localProvider as IWorldBossAreaControlStore ??
            throw Missing("world-boss area-control store");
        var worldBossRespawns = postgresRuntime?.WorldBossRespawns ??
            localProvider as IWorldBossRespawnReader ??
            throw Missing("world-boss respawn reader");
        var zodiacLevels = postgresRuntime?.ZodiacLevels ??
            localProvider as IZodiacLevelStore ??
            throw Missing("Zodiac-level store");

        return new ServerGameplayPersistenceProviders(
            measuredCharacterSnapshots,
            characterRuntime,
            ownedPets,
            experienceBoosts,
            worldBossAreaControl,
            worldBossRespawns,
            zodiacLevels);
    }

    private static InvalidOperationException Missing(string provider) =>
        new($"The gameplay {provider} was not composed.");
}
