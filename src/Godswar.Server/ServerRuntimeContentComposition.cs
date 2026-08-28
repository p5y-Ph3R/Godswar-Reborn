using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Application.Realms;
using Godswar.Server.Application.World;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Warehouse;
using Godswar.Server.State;

namespace Godswar.Server;

internal static partial class ServerRuntimeContentComposition
{
    public static Task<PinnedPetLearnedSkillContentCatalog>
        LoadLearnedSkillsAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default) =>
        ServerPetLearnedSkillContentComposition.LoadAsync(
            options,
            cancellationToken);

    public static Task<HolySpiritBalanceSnapshot>
        LoadHolySpiritBalanceAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PostgresHolySpiritBalanceSnapshotReader.LoadAsync(
            options.Storage.PostgresConnectionString,
            cancellationToken);
    }

    public static Task<WarehouseExpansionPolicySnapshot>
        LoadWarehouseExpansionPolicyAsync(
        ServerOptions options,
        GameplayItemContent items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(items);
        return PostgresWarehouseExpansionPolicySnapshotReader.LoadAsync(
            options.Storage.PostgresConnectionString,
            items.Templates,
            cancellationToken);
    }

    public static PostgresApplicationDataRuntime CreateApplicationData(
        ServerOptions options,
        IWorldContentReader world,
        GameplayItemContent items,
        PinnedPetContentCatalog pets,
        PinnedPetOwnerMergeContentCatalog petOwnerMerge,
        PinnedPetLearnedSkillContentCatalog learnedSkills,
        HolySpiritBalanceSnapshot holySpiritBalance,
        WarehouseExpansionPolicySnapshot warehouseExpansionPolicy,
        RealmCalendar realmCalendar) =>
        new(
            options.Storage.PostgresConnectionString,
            options.Storage.Outbox,
            options.Game.ZodiacEnergy.Snapshot(),
            items,
            world.Manifest.Gameplay.Sha256,
            options.Game.WorldInstances.ProcessRealmId,
            realmCalendar,
            pets,
            petOwnerMerge,
            learnedSkills,
            holySpiritBalance,
            warehouseExpansionPolicy,
            options.Storage.Reconciliation);

    public static async ValueTask<ServerCoordinationComposition>
        CreateCoordinationAsync(
            ServerOptions options,
            IWorldContentReader world,
            GameplayItemContent items,
            PinnedPetContentCatalog pets,
            PinnedPetOwnerMergeContentCatalog petOwnerMerge,
        PinnedPetLearnedSkillContentCatalog learnedSkills,
        HolySpiritBalanceSnapshot holySpiritBalance,
        WarehouseExpansionPolicySnapshot warehouseExpansionPolicy,
        RealmCalendarCatalog realmCalendars,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(learnedSkills);
        ArgumentNullException.ThrowIfNull(holySpiritBalance);
        ArgumentNullException.ThrowIfNull(warehouseExpansionPolicy);
        ArgumentNullException.ThrowIfNull(realmCalendars);
        var fingerprint = RuntimeContentFingerprint.Create(
            world.Manifest.Revision,
            items.Templates.Revision.Sha256,
            pets.Revision.Sha256,
            petOwnerMerge.Revision.Sha256,
            learnedSkills.Revision.Sha256,
            holySpiritBalance.CoordinationRevision(),
            new string('0', 64),
            realmCalendars.CoordinationRevision,
            new string('0', 64),
            warehouseExpansionPolicy.CoordinationRevision());
        return await ServerCoordinationComposition.CreateAsync(
            options,
            fingerprint,
            cancellationToken);
    }
}
