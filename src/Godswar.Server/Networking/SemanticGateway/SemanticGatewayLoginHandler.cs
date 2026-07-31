using System.Net;
using System.Security.Cryptography;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Authenticates the unchanged legacy login exchange at the local gateway.
/// The resulting bounded generation is the only bridge to the game listener;
/// credentials are never forwarded to a worker.
/// </summary>
internal sealed class SemanticGatewayLoginHandler : IClientHandler
{
    private readonly ISemanticGatewayDataSession _data;
    private readonly SemanticGatewayAdmissionAuthority _authority;
    private readonly SemanticGatewayConnectionCoordinator _connections;
    private readonly string _gamePublicHost;
    private readonly int _gamePublicPort;
    private readonly ClientSession _session;
    private bool _loginAttempted;
    private bool _redirectSent;
    private SemanticGatewayLoginGenerationLease? _generation;
    private SemanticGatewayPrincipal? _principal;

    public SemanticGatewayLoginHandler(
        ClientSession session,
        ISemanticGatewayDataSession data,
        SemanticGatewayAdmissionAuthority authority,
        SemanticGatewayConnectionCoordinator connections,
        string gamePublicHost,
        int gamePublicPort)
    {
        _session = session ??
            throw new ArgumentNullException(nameof(session));
        _data = data ??
            throw new ArgumentNullException(nameof(data));
        _authority = authority ??
            throw new ArgumentNullException(nameof(authority));
        _connections = connections ??
            throw new ArgumentNullException(nameof(connections));
        if (string.IsNullOrWhiteSpace(gamePublicHost) ||
            gamePublicHost.Length > 253)
        {
            throw new ArgumentException(
                "A bounded game redirect host is required.",
                nameof(gamePublicHost));
        }
        if (gamePublicPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(gamePublicPort));
        }

        _gamePublicHost = gamePublicHost;
        _gamePublicPort = gamePublicPort;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var packet = await _session.ReadPacketAsync(
                    cancellationToken);
                if (packet is null)
                {
                    return;
                }

                switch (packet.Opcode)
                {
                    case Opcodes.Login:
                        await HandleLoginAsync(
                            packet,
                            cancellationToken);
                        break;
                    case Opcodes.SelectServer:
                        if (_principal is null)
                        {
                            _session.Disconnect();
                            return;
                        }
                        await _session.SendAsync(
                            PacketBuilder.SendServer(),
                            cancellationToken,
                            "SemanticGatewaySendServer");
                        break;
                    case Opcodes.LoginReturnInfo:
                        if (_principal is null ||
                            _generation is null ||
                            _redirectSent ||
                            !_authority.ActivateLogin(_generation))
                        {
                            _session.Disconnect();
                            return;
                        }
                        _connections.RequestReplacement(
                            _generation.Principal.AccountId,
                            _generation.GenerationId,
                            _generation.Sequence);
                        await _session.SendAsync(
                            PacketBuilder.GameServerRedirect(
                                _gamePublicHost,
                                _gamePublicPort),
                            cancellationToken,
                            "SemanticGatewayGameRedirect");
                        _redirectSent = true;
                        break;
                    default:
                        _session.Disconnect();
                        return;
                }
            }
        }
        finally
        {
            if (!_redirectSent &&
                _generation is not null)
            {
                _authority.CancelLogin(_generation);
                _connections.RequestStop(
                    _generation.Principal.AccountId,
                    _generation.GenerationId);
            }
        }
    }

    private async Task HandleLoginAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_loginAttempted || packet.Payload.Length < 64)
            {
                await RejectAsync(cancellationToken);
                return;
            }
            _loginAttempted = true;

            var rawUsername = PacketText.ReadFixedAscii(
                packet.Payload,
                0,
                32);
            if (string.IsNullOrWhiteSpace(rawUsername))
            {
                await RejectAsync(cancellationToken);
                return;
            }

            var username = PacketText.DecodeLoginName(rawUsername);
            var password = CopyPassword(packet.Buffer);
            try
            {
                var authenticated =
                    await _data.AuthenticateAsync(
                        username,
                        password,
                        cancellationToken);
                if (authenticated is null ||
                    !TryGetRemoteAddress(out var remoteAddress))
                {
                    await RejectAsync(cancellationToken);
                    return;
                }

                SemanticGatewayPrincipal principal;
                SemanticGatewayConnectionSource source;
                try
                {
                    principal = new SemanticGatewayPrincipal(
                        authenticated.AccountId,
                        authenticated.Username);
                    source = new SemanticGatewayConnectionSource(
                        GatewayConnectionId.New(),
                        remoteAddress);
                }
                catch (ArgumentException)
                {
                    await RejectAsync(cancellationToken);
                    return;
                }

                var started = _authority.BeginLogin(
                    principal,
                    source);
                if (!started.IsStarted)
                {
                    await RejectAsync(cancellationToken);
                    return;
                }

                _generation = started.Generation;
                _principal = principal;
                _session.MarkAuthenticated();
                await _session.SendAsync(
                    PacketBuilder.ServerList(),
                    cancellationToken,
                    "SemanticGatewayServerList");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(password);
            }
        }
        finally
        {
            ClearCredentialField(packet.Buffer);
        }
    }

    private bool TryGetRemoteAddress(out IPAddress address)
    {
        if (IPEndPoint.TryParse(
                _session.RemoteEndPoint,
                out var endpoint))
        {
            address = endpoint.Address;
            return true;
        }

        address = IPAddress.None;
        return false;
    }

    private async Task RejectAsync(
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.LoginFailed(3),
            cancellationToken,
            "SemanticGatewayLoginFailed");
    }

    private static byte[] CopyPassword(byte[] packet)
    {
        const int credentialOffset = 36;
        const int credentialLength = 32;
        if (packet.Length < credentialOffset + credentialLength)
        {
            return [];
        }

        var field = packet.AsSpan(
            credentialOffset,
            credentialLength);
        var terminator = field.IndexOf((byte)0);
        var length = terminator >= 0 ? terminator : field.Length;
        return field[..length].ToArray();
    }

    private static void ClearCredentialField(byte[] packet)
    {
        const int credentialOffset = 36;
        if (packet.Length <= credentialOffset)
        {
            return;
        }

        var length = Math.Min(
            AuthenticationOptions.MaximumPasswordBytes,
            packet.Length - credentialOffset);
        CryptographicOperations.ZeroMemory(
            packet.AsSpan(credentialOffset, length));
    }
}
