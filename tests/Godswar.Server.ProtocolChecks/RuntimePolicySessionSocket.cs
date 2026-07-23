using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal sealed class RuntimePolicySessionSocket : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly TcpClient _inbound;
    private readonly PacketCipher _cipher = new();

    private RuntimePolicySessionSocket(
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

    public static async Task<RuntimePolicySessionSocket> CreateAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();
        var outbound = new TcpClient();
        await outbound.ConnectAsync(IPAddress.Loopback, port);
        var inbound = await accepted;
        return new RuntimePolicySessionSocket(
            listener,
            inbound,
            new ClientSession(outbound));
    }

    public async Task<byte[]> ReadPacketAsync(int length)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        var packet = new byte[length];
        await _inbound.GetStream().ReadExactlyAsync(
            packet,
            timeout.Token);
        _cipher.Transform(packet);
        Check.Equal(
            length,
            BinaryPrimitives.ReadUInt16LittleEndian(packet),
            "cutover packet declared length");
        return packet;
    }

    public async ValueTask DisposeAsync()
    {
        await Session.DisposeAsync();
        _inbound.Dispose();
        _listener.Stop();
    }
}
