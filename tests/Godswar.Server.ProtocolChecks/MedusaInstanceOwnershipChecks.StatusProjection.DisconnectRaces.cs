using System.Reflection;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static readonly FieldInfo SessionLifetimeField =
        RequiredPrivateField(
            typeof(ClientSession),
            "_lifetime");

    private static async Task
        CheckClaimedDisconnectSurvivesThrowingCallbackAsync()
    {
        await using var fixture =
            await MonsterPlayerHitFixture.CreateAsync("E1-Elite", 102);
        var transport = new SwitchableMedusaTransport();
        var options = new NetworkRuntimeOptions
        {
            ReliableEgressQueueItems = 4,
            ReliableEgressQueueBytes =
                LegacyProtocolLimits.MaxPacketLength * 4,
            ReliableEgressPendingItems = 8,
            ReliableEgressPendingBytes =
                LegacyProtocolLimits.MaxPacketLength * 4,
            ReliableWriteTimeoutMilliseconds = 10_000
        };
        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        _ = JoinMedusaHandlerMember(
            fixture,
            session,
            characterId: 102);
        transport.BlockWrites();
        Check.True(
            session.TryAdmitExact(
                new byte[] { 4, 0, 0x41, 0x7D },
                out var completion),
            "throwing-callback fixture admits one reliable write");
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));
        var lifetime = (CancellationTokenSource)(
            SessionLifetimeField.GetValue(session) ??
            throw new InvalidOperationException(
                "Client session lifetime was unavailable."));
        using var registration = lifetime.Token.Register(
            static () => throw new InvalidOperationException(
                "simulated hostile cancellation callback"));
        var failClosedReason = new InvalidOperationException(
            "preallocated exact disconnect reason");

        Check.True(
            session.TryClaimDisconnect(),
            "the exact membership claims disconnect before callbacks run");
        session.CompleteClaimedDisconnect(failClosedReason);
        var completionFaulted = false;
        try
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            completionFaulted = true;
        }
        fixture.Registry.Remove(session);
        Check.True(
            completionFaulted &&
            session.IsDisconnected &&
            transport.IsDisconnected &&
            fixture.Map.Snapshot().All(context =>
                !ReferenceEquals(context.Session, session)),
            "a throwing cancellation callback cannot skip egress abort, transport close, or exact registry removal after the claim");
    }

    private static async Task
        CheckOrdinaryExactAttackAdmissionFailureDisconnectsAsync()
    {
        var transport = new SwitchableMedusaTransport();
        var options = new NetworkRuntimeOptions
        {
            ReliableEgressQueueItems = 1,
            ReliableEgressQueueBytes =
                LegacyProtocolLimits.MaxPacketLength,
            ReliableEgressPendingItems = 4,
            ReliableEgressPendingBytes =
                LegacyProtocolLimits.MaxPacketLength * 2,
            ReliableWriteTimeoutMilliseconds = 10_000
        };
        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        await using var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Ecs,
            itemContent: TestItemContent.Content);
        var created = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new MapId(200),
            InstanceKind.Dungeon,
            playerCapacity: 5,
            CancellationToken.None);
        var runtime = created.Runtime ??
            throw new InvalidOperationException(
                "Ordinary exact attack runtime was unavailable.");
        var character = CreateRegistryDamageCharacter(906, mapId: 200);
        _ = GameHandlerOwnershipTestFences.Bind(
            registry,
            session,
            character.AccountId,
            character);
        registry.JoinWorldInstance(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            runtime.InstanceId,
            worldReady: true,
            joinedAt: DateTimeOffset.UtcNow);
        var context = runtime.Map.Snapshot().Single(item =>
            ReferenceEquals(item.Session, session));
        var life = registry.GetPlayerLifeRevision(session);

        transport.BlockWrites();
        Check.True(
            session.TryAdmitExact(
                new byte[] { 4, 0, 0x51, 0x7D },
                out _) ,
            "ordinary egress fixture starts one active write");
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(1));
        Check.True(
            session.TryAdmitExact(
                new byte[] { 4, 0, 0x52, 0x7D },
                out _),
            "ordinary egress fixture fills its normally admissible queue");
        var sent = await InvokeExactMonsterAttackSendAsync(
            registry,
            runtime,
            context,
            life,
            context,
            life,
            new byte[] { 4, 0, 0x53, 0x7D });
        Check.True(
            !sent &&
            session.IsDisconnected &&
            transport.IsDisconnected,
            "ordinary non-Medusa exact impact admission fails closed when live reliable egress is saturated");
        registry.Remove(session);
    }
#endif
}
