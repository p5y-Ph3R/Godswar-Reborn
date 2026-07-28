using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static async Task
        CheckInterruptionBroadcastIdentityAsync()
    {
        await using var casterSocket =
            await BackhaulSessionSocket.CreateAsync();
        await using var viewerSocket =
            await BackhaulSessionSocket.CreateAsync();
        var caster = CreateCharacter("InterruptedCaster");
        var viewer = CreateCharacter("InterruptionViewer");
        viewer.Id = CharacterId + 1;
        viewer.AccountId = AccountId + 1;
        var store = new BackhaulStore(
            caster,
            [new SkillState
            {
                SkillId = checked((int)
                    BackhaulSkillCatalog.CitySkillId),
                Level = 1
            }]);
        var registry = CreateRegistry();
        registry.JoinMap(
            casterSocket.Session,
            caster.AccountId,
            caster,
            WorldObjectIds.ForPlayer(caster.Id),
            worldReady: true,
            joinedAt: TestTime);
        registry.JoinMap(
            viewerSocket.Session,
            viewer.AccountId,
            viewer,
            WorldObjectIds.ForPlayer(viewer.Id),
            worldReady: true,
            joinedAt: TestTime);
        var handler = CreateEnteredHandler(
            casterSocket.Session,
            store,
            registry,
            caster,
            backhaulSkillCastTime: TimeSpan.FromSeconds(30));

        try
        {
            await InvokePacketAsync(
                handler,
                CreateSkillCastPacket(
                    BackhaulSkillCatalog.CitySkillId,
                    caster.PositionX,
                    caster.PositionZ,
                    targetX: 1_234f,
                    targetZ: -5_678f));

            var casterVisual =
                await casterSocket.ReadPacketAsync();
            var viewerVisual =
                await viewerSocket.ReadPacketAsync();
            Check.Equal(
                LocalPlayerObjectId,
                ReadUInt32(casterVisual, 4),
                "caster sees its native local cast identity");
            Check.Equal(
                WorldObjectIds.ForPlayer(caster.Id),
                ReadUInt32(viewerVisual, 4),
                "viewer sees the caster world identity");

            await InvokePacketAsync(
                handler,
                new GamePacket(
                    Convert.FromHexString(
                        "0800BB2748140000")));

            var casterInterrupt =
                await casterSocket.ReadPacketAsync();
            var viewerInterrupt =
                await viewerSocket.ReadPacketAsync();
            Check.True(
                casterInterrupt.SequenceEqual(
                    PacketBuilder.SkillCastInterrupt(
                        LocalPlayerObjectId)),
                "caster interruption retains native local identity");
            Check.True(
                viewerInterrupt.SequenceEqual(
                    PacketBuilder.SkillCastInterrupt(
                        WorldObjectIds.ForPlayer(caster.Id))),
                "viewer interruption uses caster world identity");
            Check.True(
                !viewerInterrupt.SequenceEqual(casterInterrupt),
                "viewer never receives the caster-only local object ID");
        }
        finally
        {
            await StopHandlerAsync(handler);
            registry.Remove(casterSocket.Session);
            registry.Remove(viewerSocket.Session);
        }
    }
}
