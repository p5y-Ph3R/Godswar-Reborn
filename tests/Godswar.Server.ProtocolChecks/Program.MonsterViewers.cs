using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckMonsterViewerRegistryAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var nearOutbound = new TcpClient();
            var nearAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await nearOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var nearInbound = await nearAcceptTask;
            await using var nearSession =
                new ClientSession(new RawTcpLegacyTransport(nearOutbound));

            using var farOutbound = new TcpClient();
            var farAcceptTask = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            await farOutbound.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            using var farInbound = await farAcceptTask;
            await using var farSession =
                new ClientSession(new RawTcpLegacyTransport(farOutbound));

            var nearCharacter = CreateCharacter();
            nearCharacter.CurrentMap = 0;
            nearCharacter.PositionX = 100;
            nearCharacter.PositionZ = 100;
            var farCharacter = CreateCharacter();
            farCharacter.Id += 1;
            farCharacter.AccountId += 1;
            farCharacter.Name = "FarViewer";
            farCharacter.CurrentMap = 0;
            farCharacter.PositionX = 500;
            farCharacter.PositionZ = 500;

            var monster = CreateCapturedMonster(
                10038,
                nearCharacter.PositionX + 1,
                nearCharacter.PositionZ + 1,
                "A_normal_stub_001");
            var registry = new GameSessionRegistry();
            registry.InitializeMapMonsters(
                nearCharacter.CurrentMap,
                [monster],
                new DateTimeOffset(2026, 5, 12, 17, 56, 0, TimeSpan.FromHours(12)));
            registry.JoinMap(
                nearSession,
                nearCharacter.AccountId,
                nearCharacter,
                WorldObjectIds.ForPlayer(nearCharacter.Id));
            registry.JoinMap(
                farSession,
                farCharacter.AccountId,
                farCharacter,
                WorldObjectIds.ForPlayer(farCharacter.Id));

            await using (var nearTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             nearSession,
                             nearCharacter.CurrentMap,
                             nearCharacter.PositionX,
                             nearCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("near monster transition was unavailable"))
            {
                Check.True(
                    nearTransition.Delta.Entering.Select(entry => entry.ObjectId).SequenceEqual([monster.ObjectId]),
                    "near viewer receives the monster AOI entry");
                Check.True(
                    !registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                    "monster AOI is uncommitted before its appearance send");
                nearTransition.Commit();
            }

            await using (var farTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             farSession,
                             farCharacter.CurrentMap,
                             farCharacter.PositionX,
                             farCharacter.PositionZ,
                             timeout.Token)
                         ?? throw new InvalidOperationException("far monster transition was unavailable"))
            {
                Check.Equal(0, farTransition.Delta.Entering.Count, "far viewer receives no monster AOI entry");
                farTransition.Commit();
            }

            Check.True(
                registry.IsMonsterVisibleTo(nearSession, monster.ObjectId) &&
                !registry.IsMonsterVisibleTo(farSession, monster.ObjectId),
                "committed monster visibility differs per viewer");
            var marker = PacketBuilder.MonsterLifecycleMarker(monster.ObjectId);
            var recipients = await registry.BroadcastToMonsterViewersAsync(
                nearCharacter.CurrentMap,
                monster.ObjectId,
                marker,
                timeout.Token,
                label: "MonsterViewerScopeCheck");
            Check.Equal(1, recipients, "monster broadcast reaches only committed AOI viewers");
            var received = new byte[marker.Length];
            await nearInbound.GetStream().ReadExactlyAsync(received, timeout.Token);
            Check.Equal(0, farInbound.Available, "far viewer receives no monster broadcast bytes");
            Check.Equal(
                0,
                await registry.BroadcastToMonsterViewersAsync(
                    nearCharacter.CurrentMap,
                    monster.ObjectId,
                    marker,
                    timeout.Token,
                    excludeSession: nearSession),
                "monster broadcast exclusion can omit the only visible viewer");

            await using (var leavingTransition =
                         await registry.BeginMonsterVisibilityTransitionAsync(
                             nearSession,
                             nearCharacter.CurrentMap,
                             nearCharacter.PositionX + 200,
                             nearCharacter.PositionZ + 200,
                             timeout.Token)
                         ?? throw new InvalidOperationException("leaving monster transition was unavailable"))
            {
                Check.True(
                    leavingTransition.Delta.Leaving.SequenceEqual([monster.ObjectId]),
                    "viewer movement produces the monster AOI leave");
                Check.True(
                    registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                    "monster remains visible until its removal send commits");
                leavingTransition.Commit();
            }

            Check.True(
                !registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                "monster removal commit updates combat AOI scope");

            var removalTransition =
                await registry.BeginMonsterVisibilityTransitionAsync(
                    nearSession,
                    nearCharacter.CurrentMap,
                    nearCharacter.PositionX + 200,
                    nearCharacter.PositionZ + 200,
                    timeout.Token)
                ?? throw new InvalidOperationException("map-removal transition was unavailable");
            try
            {
                var removalStarted = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var removalTask = Task.Run(() =>
                {
                    removalStarted.SetResult();
                    registry.Remove(nearSession);
                });
                await removalStarted.Task.WaitAsync(timeout.Token);
                await Task.Delay(50, timeout.Token);
                Check.True(
                    !removalTask.IsCompleted,
                    "map removal waits for the active viewer transition lease");
                await removalTransition.DisposeAsync();
                await removalTask.WaitAsync(timeout.Token);
                Check.True(
                    !registry.IsMonsterVisibleTo(nearSession, monster.ObjectId),
                    "map removal clears viewer membership after the lease releases");
            }
            finally
            {
                await removalTransition.DisposeAsync();
            }

            registry.Remove(farSession);
        }
        finally
        {
            listener.Stop();
        }
    }
}
