using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure;
using Godswar.Server.State;

namespace Godswar.Server;

internal static class ServerRuntimeContentComposition
{
    public static Task<PinnedPetLearnedSkillContentCatalog>
        LoadLearnedSkillsAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default) =>
        ServerPetLearnedSkillContentComposition.LoadAsync(
            options,
            cancellationToken);

    public static PostgresApplicationDataRuntime CreateApplicationData(
        ServerOptions options,
        IWorldContentReader world,
        GameplayItemContent items,
        PinnedPetContentCatalog pets,
        PinnedPetOwnerMergeContentCatalog petOwnerMerge,
        PinnedPetLearnedSkillContentCatalog learnedSkills) =>
        new(
            options.Storage.PostgresConnectionString,
            options.Storage.Outbox,
            options.Game.ZodiacEnergy.Snapshot(),
            items,
            world.Manifest.Gameplay.Sha256,
            pets,
            petOwnerMerge,
            learnedSkills,
            options.Storage.Reconciliation);

    public static async ValueTask<ServerCoordinationComposition>
        CreateCoordinationAsync(
            ServerOptions options,
            IWorldContentReader world,
            GameplayItemContent items,
            PinnedPetContentCatalog pets,
            PinnedPetOwnerMergeContentCatalog petOwnerMerge,
            PinnedPetLearnedSkillContentCatalog learnedSkills,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(learnedSkills);
        var fingerprint = RuntimeContentFingerprint.Create(
            world.Manifest.Revision,
            items.Templates.Revision.Sha256,
            pets.Revision.Sha256,
            petOwnerMerge.Revision.Sha256,
            learnedSkills.Revision.Sha256);
        return await ServerCoordinationComposition.CreateAsync(
            options,
            fingerprint,
            cancellationToken);
    }
}
