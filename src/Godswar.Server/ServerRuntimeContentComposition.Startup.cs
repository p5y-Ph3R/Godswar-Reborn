using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.World;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server;

internal sealed record ServerStartupContent(
    IWorldContentReader World,
    GameplayItemContent Items,
    PinnedPetContentCatalog Pets,
    PinnedPetOwnerMergeContentCatalog PetOwnerMerge,
    PinnedPetLearnedSkillContentCatalog LearnedSkills,
    HolySpiritBalanceSnapshot HolySpiritBalance,
    WarehouseExpansionPolicySnapshot WarehouseExpansionPolicy,
    GameplayRuntimeCatalogs GameplayCatalogs);

internal static partial class ServerRuntimeContentComposition
{
    public static async Task<ServerStartupContent?> LoadStartupContentAsync(
        ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var items = await ServerItemContentComposition.LoadAsync(options);
        var pets = await ServerPetContentComposition.LoadAsync(
            options,
            items.Templates);
        var petOwnerMerge =
            await ServerPetOwnerMergeContentComposition.LoadAsync(options);
        var learnedSkills = await LoadLearnedSkillsAsync(options);
        var holySpiritBalance = await LoadHolySpiritBalanceAsync(options);
        var warehouseExpansionPolicy =
            await LoadWarehouseExpansionPolicyAsync(options, items);
        var medusaRewards =
            await PostgresMedusaRewardPolicySnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        var medusaMonsters =
            await PostgresMedusaMonsterContentSnapshotReader.LoadAsync(
                options.Storage.PostgresConnectionString);
        MedusaRewardPolicyCatalog.Install(medusaRewards);
        MedusaMonsterContentCatalog.Install(medusaMonsters);
        var world = await ServerWorldContentComposition.TryLoadAsync(options);
        if (world is null)
        {
            return null;
        }

        RuntimeContentCompatibilityValidator.Validate(
            items.Templates,
            world.Gameplay);
        return new ServerStartupContent(
            world,
            items,
            pets,
            petOwnerMerge,
            learnedSkills,
            holySpiritBalance,
            warehouseExpansionPolicy,
            GameplayRuntimeCatalogs.Create(world.Gameplay));
    }
}
