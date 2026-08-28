using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static readonly MethodInfo MedusaIslandTraversalMethod =
        FindHandlerMethod("TryApplyMedusaIslandTraversalAsync");

    private static async Task CheckLiveIslandSceneTransitionsAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            level: 90,
            transitionReady: true);
        await OpenMedusaPageAsync(fixture);
        await InvokeAsync(
            fixture.Handler,
            CreateActionPacket(
                InstanceCallerProtocol.MedusaRootSubId,
                InstanceCallerProtocol.AdvancedDifficultySubId));
        await CompleteInstanceSceneChangeAsync(fixture);
        var instanceId = GetSourceInstanceId(fixture);

        await ApplyIslandSceneTransitionAsync(
            fixture,
            new AcceptedMapMovementSegment(
                200,
                new MapTraversalPosition(-40f, 51f),
                new MapTraversalPosition(-30f, 51f)),
            expectedX: -83f,
            expectedZ: 101f,
            instanceId,
            "first component transfers to the captured second-component landing");
        await CompleteInstanceSceneChangeAsync(fixture);

        await ApplyIslandSceneTransitionAsync(
            fixture,
            new AcceptedMapMovementSegment(
                200,
                new MapTraversalPosition(-134f, 139f),
                new MapTraversalPosition(-124f, 139f)),
            expectedX: -145f,
            expectedZ: 152f,
            instanceId,
            "second component transfers to the captured final landing");
        await CompleteInstanceSceneChangeAsync(fixture);

        Check.True(
            GetSourceInstanceId(fixture) == instanceId &&
            fixture.Character.CurrentMap == 200 &&
            fixture.Character.PositionX == -145f &&
            fixture.Character.PositionZ == 152f,
            "both island scene changes preserve the exact dungeon identity and admission");
    }

    private static async Task ApplyIslandSceneTransitionAsync(
        InstanceCallerFixture fixture,
        AcceptedMapMovementSegment movement,
        float expectedX,
        float expectedZ,
        Godswar.Server.Domain.World.Instances.WorldInstanceId instanceId,
        string description)
    {
        fixture.Character.PositionX = movement.Start.X;
        fixture.Character.PositionZ = movement.Start.Z;
        fixture.Registry.UpdateCharacter(
            fixture.Session,
            fixture.Character,
            advanceWorldRevision: false);
        var before = fixture.ReadPackets().Count;
        var task = MedusaIslandTraversalMethod.Invoke(
            fixture.Handler,
            [movement, CancellationToken.None]) as Task<bool> ??
            throw new InvalidOperationException(
                "Medusa island traversal did not return Task<bool>.");
        var applied = await task;
        var packets = fixture.ReadPackets().Skip(before).ToArray();
        Check.True(
            applied &&
            fixture.Character.PositionX == expectedX &&
            fixture.Character.PositionZ == expectedZ &&
            GetSourceInstanceId(fixture) == instanceId &&
            packets.Count(packet => ReadOpcode(packet) ==
                Opcodes.SceneChange) == 1 &&
            packets.Single(packet => ReadOpcode(packet) ==
                Opcodes.SceneChange).SequenceEqual(
                    PacketBuilder.SceneChange(
                        0x1448,
                        expectedX,
                        0f,
                        expectedZ,
                        200)),
            description);
    }

    private static async Task CompleteInstanceSceneChangeAsync(
        InstanceCallerFixture fixture)
    {
        await InvokeAsync(
            fixture.Handler,
            CreateControlPacket(Opcodes.ClientReady));
        await InvokeAsync(
            fixture.Handler,
            CreatePlayerDetailRequest());
    }
}
