using System.Net;
using System.Security.Cryptography;
using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Application.Realms;
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
    private readonly ISemanticGatewayCoordination _coordination;
    private readonly TimeSpan _coordinationTimeout;
    private readonly SemanticGatewayConnectionCoordinator _connections;
    private readonly ClientSession _session;
    private readonly TimeProvider _timeProvider;
    private bool _loginAttempted;
    private bool _redirectSent;
    private RealmCatalogSnapshot? _advertisedRealms;
    private SemanticGatewayLoginGenerationLease? _generation;
    private SemanticGatewayConnectionSource? _loginSource;
    private SemanticGatewayPrincipal? _principal;
    private RealmCatalogEntry? _selectedRealm;

    public SemanticGatewayLoginHandler(
        ClientSession session,
        ISemanticGatewayDataSession data,
        ISemanticGatewayCoordination coordination,
        SemanticGatewayConnectionCoordinator connections,
        TimeSpan coordinationTimeout,
        TimeProvider? timeProvider = null)
    {
        _session = session ??
            throw new ArgumentNullException(nameof(session));
        _data = data ??
            throw new ArgumentNullException(nameof(data));
        _coordination = coordination ??
            throw new ArgumentNullException(nameof(coordination));
        _connections = connections ??
            throw new ArgumentNullException(nameof(connections));
        if (coordinationTimeout <= TimeSpan.Zero ||
            coordinationTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinationTimeout));
        }

        _coordinationTimeout = coordinationTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
                        if (!await HandleServerSelectionAsync(
                                packet,
                                cancellationToken))
                        {
                            return;
                        }
                        break;
                    case Opcodes.LoginReturnInfo:
                        if (_principal is null ||
                            _selectedRealm is null ||
                            _generation is null ||
                            !_generation.RealmGrant.Matches(
                                _selectedRealm) ||
                            _redirectSent ||
                            !await _coordination.ActivateLoginAsync(
                                _generation,
                                NewCoordinationDeadline(),
                                cancellationToken))
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
                                _selectedRealm),
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
                try
                {
                    await _coordination.CancelLoginAsync(
                        _generation,
                        NewCoordinationDeadline(),
                        CancellationToken.None);
                }
                catch
                {
                    // Cleanup is finite and best-effort. The generation has
                    // its own bounded TTL if a remote coordinator is down.
                }
                finally
                {
                    _connections.RequestStop(
                        _generation.Principal.AccountId,
                        _generation.GenerationId);
                }
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

                var realms = await _data.ReadEnabledAsync(
                    cancellationToken);
                if (realms.Entries.IsEmpty)
                {
                    _session.Disconnect();
                    return;
                }

                _advertisedRealms = realms;
                _loginSource = source;
                _principal = principal;
                _session.MarkAuthenticated();
                await _session.SendAsync(
                    PacketBuilder.ServerList(realms),
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

    private async Task<bool> HandleServerSelectionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_principal is null ||
            _loginSource is null ||
            _advertisedRealms is null ||
            _selectedRealm is not null ||
            _generation is not null ||
            !LegacyRealmSelectionPacket.TryRead(
                packet,
                out var realmId) ||
            !_advertisedRealms.TryFind(
                realmId,
                out var advertised) ||
            advertised is null)
        {
            _session.Disconnect();
            return false;
        }

        var currentRealms = await _data.ReadEnabledAsync(
            cancellationToken);
        if (!currentRealms.TryFind(realmId, out var selected) ||
            selected is null ||
            selected != advertised)
        {
            _session.Disconnect();
            return false;
        }

        var started = await _coordination.StartLoginAsync(
            _principal.Value,
            _loginSource.Value,
            new SemanticGatewayRealmGrant(selected),
            NewCoordinationDeadline(),
            cancellationToken);
        if (!started.IsStarted || started.Generation is null)
        {
            await RejectAsync(cancellationToken);
            return false;
        }

        _generation = started.Generation;
        _selectedRealm = selected;
        await _session.SendAsync(
            PacketBuilder.SendServer(selected),
            cancellationToken,
            "SemanticGatewaySendServer");
        return true;
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

    private CoordinationDeadline
        NewCoordinationDeadline() =>
        CoordinationDeadline.FromNow(
            _coordinationTimeout,
            _timeProvider);

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
