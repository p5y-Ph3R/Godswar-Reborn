using System.Buffers.Binary;
using System.Collections.Concurrent;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerWorldObjectIdChecks
{
    private static async Task CheckAccountReplacementRemovalOrderingAsync()
    {
        const int accountId = 30_001;
        await using var registry = new GameSessionRegistry();
        var oldTransport = new CaptureTransport();
        var observerTransport = new CaptureTransport();
        var contenderTransport = new CaptureTransport();
        await using var oldSession = new ClientSession(oldTransport);
        await using var nextSession =
            new ClientSession(new NoopTransport());
        await using var observer = new ClientSession(observerTransport);
        await using var contender = new ClientSession(contenderTransport);
        await using var reuser =
            new ClientSession(new NoopTransport());

        Check.True(
            registry.ReplaceAccountSession(accountId, oldSession) is null,
            "the original account session registers without replacement");
        var oldCharacter = CreateCharacter(
            1,
            1_001,
            GameDefaults.SpartaCapitalMap);
        oldCharacter.AccountId = accountId;
        var oldObjectId = registry.JoinPlayerMap(
            oldSession,
            accountId,
            oldCharacter);
        var observerCharacter = CreateCharacter(
            100,
            1_002,
            GameDefaults.SpartaCapitalMap);
        registry.JoinPlayerMap(
            observer,
            observerCharacter.AccountId,
            observerCharacter);
        var oldContext = registry
            .GetMapSessions(oldCharacter.CurrentMap)
            .Single(context =>
                ReferenceEquals(context.Session, oldSession));

        var replacement =
            registry.ReplaceAccountSessionAndDetachWorld(
                accountId,
                nextSession);
        oldSession.Disconnect();
        var detached = replacement.DetachedWorld ??
            throw new InvalidOperationException(
                "The active replaced session was not detached.");
        Check.True(
            ReferenceEquals(replacement.ReplacedSession, oldSession) &&
            registry.IsCurrentAccountSession(accountId, nextSession) &&
            !registry.IsCurrentAccountSession(accountId, oldSession),
            "account replacement fences the stale handler");
        Check.Equal(
            oldContext.WorldInstanceId,
            detached.Context.WorldInstanceId,
            "replacement captures the exact old world instance");
        Check.Equal(
            oldObjectId,
            detached.Context.ObjectId,
            "replacement captures the exact old object identity");
        Check.Throws<InvalidOperationException>(
            () => registry.GetRequiredPlayerObjectId(oldSession),
            "the stale world session is atomically detached");

        try
        {
            var contenderCharacter = CreateCharacter(
                1 + WorldObjectIds.RemotePlayerObjectIdCapacity,
                1_003,
                GameDefaults.SpartaCapitalMap);
            var contenderId = registry.JoinPlayerMap(
                contender,
                contenderCharacter.AccountId,
                contenderCharacter);
            Check.True(
                contenderId != oldObjectId,
                "the detached ID cannot be reused before removal egress");

            var recipients =
                await registry.BroadcastToWorldInstanceAsync(
                    detached.Context.WorldInstanceId,
                    PacketBuilder.RemoveWorldObjects(oldObjectId),
                    CancellationToken.None,
                    oldSession,
                    "ReplacementRemovalCheck");
            Check.Equal(
                2,
                recipients,
                "exact-instance removal reaches every remaining viewer");
            AssertSingleRemovalPacket(
                observerTransport,
                oldObjectId,
                "observer");
            AssertSingleRemovalPacket(
                contenderTransport,
                oldObjectId,
                "joining contender");
            Check.Equal(
                0,
                oldTransport.Writes.Count,
                "the detached transport is excluded from removal egress");
        }
        finally
        {
            registry.ReleaseDetachedPlayerWorld(detached);
        }

        var reuserCharacter = CreateCharacter(
            1,
            1_004,
            GameDefaults.SpartaCapitalMap);
        Check.Equal(
            oldObjectId,
            registry.JoinPlayerMap(
                reuser,
                reuserCharacter.AccountId,
                reuserCharacter),
            "the old ID becomes reusable only after removal egress");

        registry.Remove(observer);
        registry.Remove(contender);
        registry.Remove(reuser);
    }

    private static void AssertSingleRemovalPacket(
        CaptureTransport transport,
        uint expectedObjectId,
        string recipient)
    {
        var packet = transport.Writes.Single().ToArray();
        new PacketCipher().Transform(packet);
        Check.Equal(
            (ushort)12,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            $"{recipient} removal packet length");
        Check.Equal(
            (ushort)10024,
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2)),
            $"{recipient} removal opcode");
        Check.Equal(
            1u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(4)),
            $"{recipient} removal object count");
        Check.Equal(
            expectedObjectId,
            BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(8)),
            $"{recipient} exact removed object ID");
    }

    private sealed class CaptureTransport : ILegacyByteTransport
    {
        private readonly ConcurrentQueue<byte[]> _writes = [];

        public string RemoteEndPoint => "player-object-id-capture";

        public IReadOnlyList<byte[]> Writes => _writes.ToArray();

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            _writes.Enqueue(source.ToArray());
            return ValueTask.CompletedTask;
        }

        public void MarkAuthenticated()
        {
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
