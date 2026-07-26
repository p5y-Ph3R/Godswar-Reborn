using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private static async Task
        CheckFirstRejectedInputUsesServerStateAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session =
            new ClientSession(transport);
        var character = CreateCharacter(
            CharacterId + 20,
            AccountId + 20,
            "RealtimeFirstReject");
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [],
            TestTime);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var handler = CreateReadyHandler(
            session,
            registry,
            character);

        await ProcessTickAsync(handler);
        var initial = transport.Snapshots.Single();
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initial.WorldGeneration,
                legacyState: 0xDEAD_0001,
                x: 100f,
                z: 100f,
                TimeSpan.FromMilliseconds(100)));
        var effects = await ProcessTickAsync(handler);
        var correction =
            GetEffectPacket(effects, "ReliableCorrection")
            ?? throw new InvalidOperationException(
                "First rejected input produced no correction.");
        var correctionState =
            BinaryPrimitives.ReadUInt32LittleEndian(
                correction.AsSpan(4));
        Check.Equal(
            0x0002_0000u,
            correctionState & 0xFFFF_0000u,
            "first rejected input correction retains server-owned neutral state");
        Check.Equal(
            0x0002_0000u,
            transport.Snapshots.Last().LegacyState,
            "first rejected input cannot alter snapshot opaque state");
        Check.True(
            character.PositionX == 0f &&
            character.PositionZ == 0f,
            "first rejected input cannot alter authoritative position");

        registry.Remove(session);
    }
}
