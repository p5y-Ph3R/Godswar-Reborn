using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class LoginClientHandler : IClientHandler
{
    private readonly ClientSession _session;
    private readonly IGameStore _store;
    private readonly ServerOptions _options;

    public LoginClientHandler(ClientSession session, IGameStore store, ServerOptions options)
    {
        _session = session;
        _store = store;
        _options = options;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var packet = await _session.ReadPacketAsync(cancellationToken);
            if (packet is null)
            {
                return;
            }

            await HandlePacketAsync(packet, cancellationToken);
        }
    }

    private async Task HandlePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        LogReceived(packet);

        switch (packet.Opcode)
        {
            case Opcodes.Login:
                await HandleLoginAsync(packet, cancellationToken);
                break;
            case Opcodes.SelectServer:
                await _session.SendAsync(PacketBuilder.SendServer(), cancellationToken, "SendServer");
                break;
            case Opcodes.LoginReturnInfo:
                await _session.SendAsync(
                    PacketBuilder.GameServerRedirect(_options.Game.PublicHost, _options.Game.Port),
                    cancellationToken,
                    "GameServerRedirect");
                break;
            default:
                Console.WriteLine(
                    $"[login] unknown {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} {packet.ToHexPreview()}");
                break;
        }
    }

    private async Task HandleLoginAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        var payload = packet.Payload;
        var rawUsername = PacketText.ReadFixedAscii(payload, 0, 32);
        var username = PacketText.DecodeLoginName(rawUsername);
        var password = PacketText.ReadFixedAscii(payload, 32, 32);

        if (string.IsNullOrWhiteSpace(username))
        {
            await _session.SendAsync(PacketBuilder.LoginFailed(3), cancellationToken, "LoginFailed");
            return;
        }

        await _store.LoginOrCreateAccountAsync(username, password, cancellationToken);
        _session.MarkAuthenticated();
        Console.WriteLine($"[login] accepted {username}");
        await _session.SendAsync(PacketBuilder.ServerList(), cancellationToken, "ServerList");
    }

    private static void LogReceived(GamePacket packet)
    {
        Console.WriteLine(
            $"[login] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} hex={packet.ToHexPreview(32)}");
    }
}
