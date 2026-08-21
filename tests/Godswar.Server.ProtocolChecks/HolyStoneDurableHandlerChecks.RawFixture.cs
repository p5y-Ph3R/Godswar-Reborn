using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.World;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task<RawHolyStoneFixture>
        CreateRawFixtureAsync(
            string? initialKitBag = null,
            Func<GameCharacter, GameCharacter?>? storeMutation = null,
            int? initialGold = null,
            uint requestNpcId = HolyStoneProtocol.SpartaNpcId,
            HolyStoneExecutionResult? durableExecutionResult = null,
            HolyStoneCommandOperation durableOperation =
                HolyStoneCommandOperation.Upgrade,
            bool requiresDurablePlayerCommands = false,
            bool hasLocalLegacyAuthenticationAccess = false)
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var snapshot = WithHolyStoneState(
            baseSnapshot,
            WeaponBefore,
            StoneBefore,
            physicalAttack: 400);
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot)
            ?? throw new InvalidOperationException(
                "Raw Holy Stone fixture did not hydrate.");
        var live = hydrated.Character;
        live.PositionX = 12.5f;
        live.PositionZ = -33.25f;
        if (initialKitBag is not null)
        {
            live.KitBag = initialKitBag;
        }
        if (initialGold.HasValue)
        {
            live.Gold = initialGold.Value;
        }

        var npc = CreateHolyStoneNpc(
            live,
            requestNpcId);
        var transport = new RawHolyStoneCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            baseSnapshot.AccountId,
            live);
        registry.JoinPlayerMap(
            session,
            baseSnapshot.AccountId,
            live,
            worldReady: false);
        var store = new HolyStoneStore();
        if (storeMutation is not null)
        {
            store.ResultFactory = () => storeMutation(live);
        }
        var executor = durableExecutionResult is null
            ? null
            : new HolyStoneExecutor(
                HolyStoneExecutionResult.ReplayNotFound(),
                durableExecutionResult,
                durableOperation);
        var localAccess = hasLocalLegacyAuthenticationAccess
            ? LegacyAuthenticationAccess.Create(
                new ValidatedServerRuntimeProfile(
                    ServerRuntimeProfileKind.LocalDevelopment,
                    GameStorageProviderKind.Postgres,
                    ServerListenerTransport.RawTcp,
                    AllowsLegacyAuthentication: true))
            : null;
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            new HolyStoneSnapshotReader(snapshot, fails: false),
            CreateWorldContent(npc),
            holyStoneCommands: executor,
            legacyAuthenticationAccess: localAccess,
            itemContent: TestItemContent.Content);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                baseSnapshot.AccountId,
                "raw-holy-stone-check"));
        SetField(handler, "_character", live);
        if (requiresDurablePlayerCommands)
        {
            SetField(
                handler,
                "_requiresDurablePlayerCommands",
                true);
        }

        var catalog =
            await registry.PublishMapNpcDefinitionsAsync(
                live.CurrentMap,
                [npc],
                originSession: null,
                CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var tracker =
            GetField<WorldSectorVisibilityTracker<
                Godswar.Server.Domain.World.Content.NpcSpawnDefinition>>(
                handler,
                "_npcVisibility")
            ?? throw new InvalidOperationException(
                "Raw Holy Stone NPC visibility was not installed.");
        Check.True(
            tracker.TryCalculate(
                live.PositionX,
                live.PositionZ,
                out var delta),
            "raw Holy Stone NPC visibility calculates");
        tracker.Commit(delta);

        return new RawHolyStoneFixture(
            session,
            transport,
            handler,
            store,
            registry,
            executor,
            live);
    }

    private sealed record RawHolyStoneFixture(
        ClientSession Session,
        RawHolyStoneCaptureTransport Transport,
        GameClientHandler Handler,
        HolyStoneStore Store,
        GameSessionRegistry Registry,
        HolyStoneExecutor? Executor,
        GameCharacter LiveCharacter) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
