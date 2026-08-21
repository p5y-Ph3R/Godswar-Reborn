using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private static async Task CheckHostileStatusBlocksRealtimeMovementAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session = new ClientSession(transport);
        await using var attackerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var character = TrainingDummyHostileStatusTestFixture
            .CreateDummy();
        var attacker = TrainingDummyHostileStatusTestFixture
            .CreateAttacker(
                profession: 1,
                id: 9_350,
                name: "RealtimeFreezeCaster");
        var registry = TrainingDummyHostileStatusTestFixture
            .CreateRegistry();
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [],
            TestTime);
        registry.JoinPlayerMap(
            attackerSocket.Session,
            attacker.AccountId,
            attacker);
        registry.JoinPlayerMap(
            session,
            character.AccountId,
            character);
        var handler = CreateReadyHandler(
            session,
            registry,
            character);

        var initialEffects = await ProcessTickAsync(handler);
        AssertNoMovementEffects(initialEffects, "Frozen baseline");
        var initial = transport.Snapshots.Single();
        var applied = await TrainingDummyHostileStatusTestFixture.ApplyAsync(
            registry,
            attackerSocket.Session,
            attacker,
            session,
            character,
            skillId: 350,
            DateTimeOffset.UtcNow,
            shouldApply: true);
        Check.True(
            applied.Accepted &&
            applied.Targets.Single().Application.Applied,
            "Frozen is applied before realtime input");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initial.WorldGeneration,
                legacyState: 0xF20A_0001,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        var blocked = await ProcessTickAsync(handler);

        Check.True(
            GetEffectPacket(blocked, "ViewerMovement") is null &&
            character.PositionX == 148f &&
            character.PositionZ == -154f,
            "Frozen blocks authoritative realtime movement and viewer egress");
        registry.Remove(attackerSocket.Session);
        registry.Remove(session);
    }
}
