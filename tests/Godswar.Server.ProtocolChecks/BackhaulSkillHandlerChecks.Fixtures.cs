using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private readonly record struct PositionWrite(
        byte MapId,
        float X,
        float Z);

    private readonly record struct VitalsWrite(
        int CurrentHp,
        int CurrentMp,
        long Revision);

    private sealed class BackhaulStore : GameStoreTestStub
    {
        private readonly GameCharacter _character;
        private readonly IReadOnlyList<SkillState> _skills;

        public BackhaulStore(
            GameCharacter character,
            IReadOnlyList<SkillState> skills)
        {
            _character = character;
            _skills = skills;
        }

        public List<PositionWrite> PositionWrites { get; } = [];

        public List<VitalsWrite> VitalsWrites { get; } = [];

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            Check.True(
                accountId == _character.AccountId &&
                characterId == _character.Id,
                "backhaul persists the active identity");
            PositionWrites.Add(new PositionWrite(
                currentMap,
                positionX,
                positionZ));
            return Task.CompletedTask;
        }

        public override Task SaveCharacterVitalsAsync(
            int accountId,
            int characterId,
            int currentHp,
            int currentMp,
            long vitalsRevision,
            CancellationToken cancellationToken = default)
        {
            Check.True(
                accountId == _character.AccountId &&
                characterId == _character.Id,
                "backhaul vitals persist the active identity");
            VitalsWrites.Add(new VitalsWrite(
                currentHp,
                currentMp,
                vitalsRevision));
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(_skills);
    }

    private sealed class BackhaulSessionSocket : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TcpClient _inbound;
        private readonly PacketCipher _receiveCipher = new();

        private BackhaulSessionSocket(
            TcpListener listener,
            TcpClient inbound,
            ClientSession session)
        {
            _listener = listener;
            _inbound = inbound;
            Session = session;
        }

        public ClientSession Session { get; }

        public int Available => _inbound.Available;

        public static async Task<BackhaulSessionSocket> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptTcpClientAsync();
            var outbound = new TcpClient();
            await outbound.ConnectAsync(IPAddress.Loopback, port);
            var inbound = await accepted;
            return new BackhaulSessionSocket(
                listener,
                inbound,
                new ClientSession(
                    new RawTcpLegacyTransport(outbound)));
        }

        public async Task<byte[]> ReadPacketAsync()
        {
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(5));
            var lengthBytes = new byte[sizeof(ushort)];
            await _inbound.GetStream().ReadExactlyAsync(
                lengthBytes,
                timeout.Token);
            _receiveCipher.Transform(lengthBytes);
            var length =
                BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
            Check.True(
                length >= 4 && length <= ushort.MaxValue,
                "backhaul packet has a bounded declared length");

            var packet = new byte[length];
            lengthBytes.CopyTo(packet, 0);
            await _inbound.GetStream().ReadExactlyAsync(
                packet.AsMemory(sizeof(ushort)),
                timeout.Token);
            _receiveCipher.Transform(packet.AsSpan(sizeof(ushort)));
            return packet;
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            _inbound.Dispose();
            _listener.Stop();
        }
    }
}
