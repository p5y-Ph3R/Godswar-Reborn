using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentForgeDurableHandlerChecks
{
    private static async Task
        CheckLegacyRawProjectionPreservesLiveVitalsAsync()
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var beforeBag = CreateBag(
            EquipmentBefore,
            PrimaryBefore);
        var afterBag = CreateBag(
            EquipmentAfter,
            PrimaryAfter);
        var liveSnapshot = WithWalletAndBag(
            baseSnapshot,
            silver: 1_000,
            beforeBag);
        var persistedSnapshot = WithWalletAndBag(
            baseSnapshot,
            silver: 800,
            afterBag);
        var live = CharacterLoadSnapshotHydrator
            .Hydrate(liveSnapshot)?.Character
            ?? throw new InvalidOperationException(
                "Legacy Forge live fixture did not hydrate.");
        var persisted = CharacterLoadSnapshotHydrator
            .Hydrate(persistedSnapshot)?.Character
            ?? throw new InvalidOperationException(
                "Legacy Forge persisted fixture did not hydrate.");

        live.MaxHp = 9_000;
        live.CurrentHp = 6_815;
        live.MaxMp = 1_200;
        live.CurrentMp = 932;
        live.VitalsRevision = 77;
        live.PositionX = 321.25f;
        live.PositionZ = -222.5f;
        live.Gold = 888;
        live.CalculatedStats = CharacterStats.FromCharacter(live);
        var calculatedStats = live.CalculatedStats;
        persisted.MaxHp = 1_500;
        persisted.CurrentHp = 1_500;
        persisted.MaxMp = 177;
        persisted.CurrentMp = 177;

        var transport = new ScriptedLegacyByteTransport();
        await using var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            baseSnapshot.AccountId,
            live);
        var store = new ForgeStore
        {
            Result = new ForgeTransactionResult(
                ForgeTransactionStatus.Succeeded,
                persisted,
                MaterialType: 2,
                Probability: 100,
                SilverSpent: 200,
                EquipmentBefore,
                EquipmentAfter)
        };
        var localAccess = LegacyAuthenticationAccess.Create(
            new ValidatedServerRuntimeProfile(
                ServerRuntimeProfileKind.LocalDevelopment,
                GameStorageProviderKind.Postgres,
                ServerListenerTransport.RawTcp,
                AllowsLegacyAuthentication: true));
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            new ForgeSnapshotReader(
                persistedSnapshot,
                fails: false),
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: localAccess);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                baseSnapshot.AccountId,
                "legacy-forge-vitals-check"));
        SetField(handler, "_character", live);
        SetField(handler, "_requiresDurablePlayerCommands", true);
        StageForgeSelections(
            handler,
            baseSnapshot.AccountId,
            live.Id);

        await InvokeForgeStartAsync(handler);

        var projected = typeof(GameClientHandler).GetField(
                "_character",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)?.GetValue(handler)
            as GameCharacter;
        Check.Equal(1, store.ForgeCount, "legacy Forge commits once");
        Check.True(
            ReferenceEquals(live, projected),
            "legacy Forge retains the active character projection");
        Check.Equal(afterBag, live.KitBag, "legacy Forge reloads bag");
        Check.Equal(800, live.Silver, "legacy Forge reloads Silver");
        Check.Equal(888, live.Gold, "legacy Forge preserves Gold");
        Check.Equal(9_000, live.MaxHp, "legacy Forge preserves live maximum HP");
        Check.Equal(6_815, live.CurrentHp, "legacy Forge preserves live current HP");
        Check.Equal(1_200, live.MaxMp, "legacy Forge preserves live maximum MP");
        Check.Equal(932, live.CurrentMp, "legacy Forge preserves live current MP");
        Check.Equal(77L, live.VitalsRevision, "legacy Forge preserves vitals revision");
        Check.Equal(321.25f, live.PositionX, "legacy Forge preserves live X");
        Check.Equal(-222.5f, live.PositionZ, "legacy Forge preserves live Z");
        Check.True(
            ReferenceEquals(calculatedStats, live.CalculatedStats),
            "legacy Forge preserves calculated combat stats");

        registry.Remove(session);
    }
}
