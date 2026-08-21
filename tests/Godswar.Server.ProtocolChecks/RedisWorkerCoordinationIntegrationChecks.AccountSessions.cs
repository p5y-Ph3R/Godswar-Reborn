using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Redis;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RedisWorkerCoordinationIntegrationChecks
{
    private const int CrossRealmAccountId = 700_002;
    private const int ForeignAccountId = 700_003;
    private const int TempestCharacterId = 700_019;
    private const int DwargonCharacterId = 700_020;

    private static async Task CheckCrossRealmAccountReplacementAsync(
        RedisCoordinationExecutor executor,
        RedisCoordinationKeyBuilder keys,
        CoordinatedWorldRoute tempestRoute,
        ServerNodeId tempestNodeId,
        Guid tempestBootId,
        ISet<string> cleanup)
    {
        var dwargonRoute = new CoordinatedWorldRoute(
            new RealmId(2),
            tempestRoute.MapId,
            new WorldInstanceId(Guid.NewGuid()));
        var dwargonNodeId = new ServerNodeId("b17-worker-dwargon");
        var dwargonBootId = Guid.NewGuid();
        cleanup.Add(keys.Worker(dwargonNodeId));
        cleanup.Add(keys.RealmContent(dwargonRoute.RealmId));
        cleanup.Add(keys.Route(dwargonRoute.WorldInstanceId));
        cleanup.Add(keys.PlayerAccount(CrossRealmAccountId));
        cleanup.Add(keys.PlayerAccount(ForeignAccountId));
        cleanup.Add(keys.Player(TempestCharacterId));
        cleanup.Add(keys.Player(DwargonCharacterId));

        await using var tempest = new RedisWorkerCoordination(
            executor,
            keys,
            capacity: 512,
            maximumConcurrency: 16);
        await using var dwargon = new RedisWorkerCoordination(
            executor,
            keys,
            capacity: 512,
            maximumConcurrency: 16);
        var registration = await dwargon.RegisterWorkerAsync(
            Registration(
                dwargonNodeId,
                dwargonBootId,
                dwargonRoute,
                "content-new"),
            TimeSpan.FromSeconds(20),
            Deadline);
        Check.True(
            registration.Succeeded,
            "Dwargon worker registers for the cross-realm account race");

        var tempestRequest = CrossRealmPlayerRequest(
            CrossRealmAccountId,
            TempestCharacterId,
            tempestNodeId,
            tempestBootId,
            tempestRoute);
        var dwargonRequest = CrossRealmPlayerRequest(
            CrossRealmAccountId,
            DwargonCharacterId,
            dwargonNodeId,
            dwargonBootId,
            dwargonRoute);
        var installTasks = new[]
        {
            tempest.InstallPlayerLeaseAsync(
                tempestRequest,
                TimeSpan.FromSeconds(30),
                Deadline).AsTask(),
            dwargon.InstallPlayerLeaseAsync(
                dwargonRequest,
                TimeSpan.FromSeconds(30),
                Deadline).AsTask()
        };
        var installs = await Task.WhenAll(installTasks);
        Check.True(
            installs.All(static result => result.Succeeded),
            "concurrent Tempest and Dwargon installs serialize successfully");

        var tempestLookup = await tempest.FindPlayerLeaseAsync(
            TempestCharacterId,
            Deadline);
        var dwargonLookup = await dwargon.FindPlayerLeaseAsync(
            DwargonCharacterId,
            Deadline);
        Check.True(
            tempestLookup.IsFound != dwargonLookup.IsFound,
            "one account has exactly one surviving cross-realm player lease");

        var tempestWon = tempestLookup.IsFound;
        var survivor = installs[tempestWon ? 0 : 1].Lease ??
            throw new InvalidOperationException(
                "The surviving cross-realm install had no lease.");
        var loser = installs[tempestWon ? 1 : 0].Lease ??
            throw new InvalidOperationException(
                "The replaced cross-realm install had no lease.");
        var survivorCoordination = tempestWon ? tempest : dwargon;
        var loserCoordination = tempestWon ? dwargon : tempest;

        Check.Equal(
            (int)CoordinationOperationStatus.NotFound,
            (int)await loserCoordination.ReleasePlayerLeaseAsync(
                loser,
                Deadline),
            "replaced realm release cannot clear the newer account session");
        var survivorAfterStaleRelease =
            await survivorCoordination.FindPlayerLeaseAsync(
                survivor.CharacterId,
                Deadline);
        Check.True(
            survivorAfterStaleRelease.IsFound &&
            survivorAfterStaleRelease.Lease!.LeaseToken ==
                survivor.LeaseToken,
            "stale cross-realm release preserves the surviving lease");

        var staleRestore =
            await loserCoordination.InstallPlayerLeaseAsync(
                (tempestWon ? dwargonRequest : tempestRequest) with
                {
                    AllowAccountReplacement = false
                },
                TimeSpan.FromSeconds(30),
                Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)staleRestore.Status,
            "replaced runtime recovery cannot evict its account successor");
        survivorAfterStaleRelease =
            await survivorCoordination.FindPlayerLeaseAsync(
                survivor.CharacterId,
                Deadline);
        Check.True(
            survivorAfterStaleRelease.IsFound &&
            survivorAfterStaleRelease.Lease!.LeaseToken ==
                survivor.LeaseToken,
            "failed stale recovery preserves the surviving account lease");

        var foreignAccount = await loserCoordination.InstallPlayerLeaseAsync(
            CrossRealmPlayerRequest(
                ForeignAccountId,
                survivor.CharacterId,
                loser.NodeId,
                loser.WorkerBootId,
                loser.Route,
                generation: 2),
            TimeSpan.FromSeconds(30),
            Deadline);
        Check.Equal(
            (int)CoordinationOperationStatus.Conflict,
            (int)foreignAccount.Status,
            "higher generation cannot change a character's account owner");
        var survivorAfterForeignAccount =
            await survivorCoordination.FindPlayerLeaseAsync(
                survivor.CharacterId,
                Deadline);
        Check.True(
            survivorAfterForeignAccount.IsFound &&
            survivorAfterForeignAccount.Lease!.AccountId ==
                CrossRealmAccountId,
            "rejected account reassignment preserves the account index");

        var renewed = await survivorCoordination.RenewPlayerLeaseAsync(
            survivor,
            survivor.Route,
            CoordinatedPresenceState.Online,
            TimeSpan.FromSeconds(30),
            Deadline);
        Check.True(
            renewed.Succeeded &&
            renewed.Lease!.Presence == CoordinatedPresenceState.Online,
            "surviving account lease remains renewable after replacement");
        Check.Equal(
            (int)CoordinationOperationStatus.Applied,
            (int)await survivorCoordination.ReleasePlayerLeaseAsync(
                renewed.Lease!,
                Deadline),
            "surviving account lease releases its exact account index");
        var accountIndexExists = await executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Health,
            Deadline,
            database => database.KeyExistsAsync(
                keys.PlayerAccount(CrossRealmAccountId)));
        Check.True(
            !accountIndexExists,
            "exact release removes the account player index");

        Check.Equal(
            (int)CoordinationOperationStatus.Applied,
            (int)await dwargon.ReleaseWorkerAsync(
                registration.Lease!.Value with
                {
                    State = CoordinatedWorkerState.Draining
                },
                Deadline),
            "cross-realm test releases the Dwargon worker exactly");
    }

    private static PlayerLeaseInstallRequest CrossRealmPlayerRequest(
        int accountId,
        int characterId,
        ServerNodeId nodeId,
        Guid bootId,
        CoordinatedWorldRoute route,
        long generation = 1) =>
        new()
        {
            AccountId = accountId,
            CharacterId = characterId,
            Ownership = new PlayerOwnershipFence(
                Guid.NewGuid(),
                generation),
            LeaseToken = Guid.NewGuid(),
            NodeId = nodeId,
            WorkerBootId = bootId,
            Route = route,
            Presence = CoordinatedPresenceState.EnteringWorld
        };
}
