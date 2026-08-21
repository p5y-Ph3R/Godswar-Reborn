using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Realms;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Security.Authentication;

namespace Godswar.Server.Game;

internal sealed class LoginClientHandler : IClientHandler
{
    private readonly ClientSession _session;
    private readonly ILegacyAccountLoginStore _legacyAccounts;
    private readonly ServerOptions _options;
    private readonly AccountAuthenticationService? _authentication;
    private readonly SecureGameTarget? _gameTarget;
    private readonly IGameTicketStore? _ticketStore;
    private readonly LegacyAuthenticationAccess?
        _legacyAuthenticationAccess;
    private readonly IRealmCatalogReader? _realmCatalog;
    private AccountIdentity? _authenticatedAccount;
    private RealmCatalogSnapshot? _advertisedRealms;
    private SecureLoginGeneration? _loginGeneration;
    private RealmCatalogEntry? _selectedRealm;
    private bool _grantCommitted;
    private bool _loginAttempted;

    public LoginClientHandler(
        ClientSession session,
        ILegacyAccountLoginStore legacyAccounts,
        ServerOptions options,
        AccountAuthenticationService? authentication = null,
        IGameTicketStore? ticketStore = null,
        SecureGameTarget? gameTarget = null,
        LegacyAuthenticationAccess?
            legacyAuthenticationAccess = null,
        IRealmCatalogReader? realmCatalog = null)
    {
        _session = session;
        _legacyAccounts = legacyAccounts ??
            throw new ArgumentNullException(nameof(legacyAccounts));
        _options = options;
        if ((authentication is null) != (ticketStore is null) ||
            (ticketStore is null) != (gameTarget is null))
        {
            throw new ArgumentException(
                "Secure login authentication, tickets, and game target must be configured together.");
        }
        _authentication = authentication;
        _ticketStore = ticketStore;
        _gameTarget = gameTarget;
        _legacyAuthenticationAccess =
            legacyAuthenticationAccess;
        _realmCatalog = realmCatalog;
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

                using var activity = ServerActivity.StartPacket(
                    ServerTraceOperation.LoginPacket,
                    "login",
                    _session.IsSecure ? "tls" : "raw_tcp");
                try
                {
                    await HandlePacketAsync(packet, cancellationToken);
                    activity.Complete(
                        ServerTraceOutcome.Accepted);
                }
                catch (OperationCanceledException)
                {
                    activity.Complete(
                        ServerTraceOutcome.Cancelled);
                    throw;
                }
                catch
                {
                    activity.Complete(
                        ServerTraceOutcome.Faulted);
                    throw;
                }
            }
        }
        finally
        {
            if (!_grantCommitted &&
                _loginGeneration is not null &&
                _ticketStore is not null)
            {
                await _ticketStore.RevokeGenerationAsync(
                    _loginGeneration,
                    SecureTicketOperationDeadline.Default,
                    CancellationToken.None);
            }
        }
    }

    private async Task HandlePacketAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        if (_session.AllowsPayloadDiagnostics)
        {
            LogReceived(packet);
        }

        switch (packet.Opcode)
        {
            case Opcodes.Login:
                await HandleLoginAsync(packet, cancellationToken);
                break;
            case Opcodes.SelectServer:
                await HandleServerSelectionAsync(
                    packet,
                    cancellationToken);
                break;
            case Opcodes.LoginReturnInfo:
                await HandleGameRedirectAsync(cancellationToken);
                break;
            default:
                if (_session.AllowsPayloadDiagnostics)
                {
                    Console.WriteLine(
                        $"[login] unknown {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} {packet.ToHexPreview()}");
                }
                break;
        }
    }

    private async Task HandleLoginAsync(GamePacket packet, CancellationToken cancellationToken)
    {
        try
        {
            if (_loginAttempted)
            {
                _session.Disconnect();
                return;
            }
            _loginAttempted = true;

            var payload = packet.Payload;
            if (payload.Length < 64)
            {
                await SendGenericFailureAsync(cancellationToken);
                return;
            }

            var rawUsername = PacketText.ReadFixedAscii(payload, 0, 32);
            if (string.IsNullOrWhiteSpace(rawUsername))
            {
                await SendGenericFailureAsync(cancellationToken);
                return;
            }
            var username = PacketText.DecodeLoginName(rawUsername);
            if (!_session.IsSecure)
            {
                if (_legacyAuthenticationAccess is null)
                {
                    ServerProfileMetrics
                        .RecordLegacyAuthenticationAttempt(
                            "login",
                            "blocked");
                    Console.Error.WriteLine(
                        "[security] rejected legacy authentication " +
                        "endpoint=login reason=profile");
                    _session.Disconnect();
                    return;
                }

                ServerProfileMetrics
                    .RecordLegacyAuthenticationAttempt(
                        "login",
                        "allowed");
                var password = PacketText.ReadFixedAscii(payload, 32, 32);
                _authenticatedAccount =
                    await _legacyAccounts.LoginOrCreateLegacyAccountAsync(
                        username,
                        password,
                        cancellationToken);
                _session.MarkAuthenticated();
                if (_session.AllowsPayloadDiagnostics)
                {
                    Console.WriteLine($"[login] accepted {username}");
                }
                await SendServerListAsync(cancellationToken);
                return;
            }

            if (_authentication is null ||
                _ticketStore is null ||
                _gameTarget is null)
            {
                _session.Disconnect();
                return;
            }

            var passwordBytes = CopyPasswordBytes(
                packet.Buffer.AsSpan(36, 32));
            try
            {
                var result = await _authentication.AuthenticateAsync(
                    username,
                    passwordBytes,
                    cancellationToken);
                if (!result.IsAccepted)
                {
                    await SendGenericFailureAsync(cancellationToken);
                    return;
                }

                var generation = await _ticketStore.BeginLoginAsync(
                    result.Account!.Id,
                    result.Account.Username,
                    SecureTicketOperationDeadline.Default,
                    cancellationToken);
                if (!generation.IsStarted)
                {
                    await SendGenericFailureAsync(cancellationToken);
                    return;
                }

                _authenticatedAccount = result.Account;
                _loginGeneration = generation.Generation;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(passwordBytes);
            }

            _session.MarkAuthenticated();
            await SendServerListAsync(cancellationToken);
        }
        finally
        {
            ClearAvailableCredentialField(packet.Buffer);
        }
    }

    private async Task HandleGameRedirectAsync(
        CancellationToken cancellationToken)
    {
        if (_authenticatedAccount is null)
        {
            _session.Disconnect();
            return;
        }

        if (_realmCatalog is not null && _selectedRealm is null)
        {
            _session.Disconnect();
            return;
        }

        if (!_session.IsSecure)
        {
            if (_selectedRealm is not null)
            {
                await _session.SendAsync(
                    PacketBuilder.GameServerRedirect(_selectedRealm),
                    cancellationToken,
                    "GameServerRedirect");
                return;
            }

            await _session.SendAsync(
                PacketBuilder.GameServerRedirect(
                    _options.Game.PublicHost,
                    _options.Game.ResolvePublicPort()),
                cancellationToken,
                "GameServerRedirect");
            return;
        }

        if (_grantCommitted ||
            _loginGeneration is null ||
            _ticketStore is null ||
            _gameTarget is null ||
            _session.SecureConnectionContext is not { } context)
        {
            _session.Disconnect();
            return;
        }

        var issued = await _ticketStore.IssueAsync(
            _loginGeneration,
            context,
            _gameTarget,
            SecureTicketOperationDeadline.Default,
            cancellationToken);
        if (!issued.IsIssued)
        {
            _session.Disconnect();
            return;
        }

        await using var lease = issued.Lease!;
        await _session.SendGameGrantAsync(
            lease.Grant,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.GameServerRedirect(
                _gameTarget.RouteHost,
                _gameTarget.RoutePort),
            cancellationToken,
            "GameServerRedirect");
        if (!await lease.CommitAsync(
                SecureTicketOperationDeadline.Default,
                cancellationToken))
        {
            _session.Disconnect();
            throw new SecureTransportException(
                "The game grant expired or was invalidated after redirect.");
        }

        _grantCommitted = true;
    }

    private async Task SendServerListAsync(
        CancellationToken cancellationToken)
    {
        if (_realmCatalog is null)
        {
            await _session.SendAsync(
                PacketBuilder.ServerList(),
                cancellationToken,
                "ServerList");
            return;
        }

        if (_advertisedRealms is not null)
        {
            _session.Disconnect();
            return;
        }

        _advertisedRealms = await _realmCatalog.ReadEnabledAsync(
            cancellationToken);
        if (_advertisedRealms.Entries.IsEmpty)
        {
            _session.Disconnect();
            return;
        }

        await _session.SendAsync(
            PacketBuilder.ServerList(_advertisedRealms),
            cancellationToken,
            "ServerList");
    }

    private async Task HandleServerSelectionAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_authenticatedAccount is null)
        {
            _session.Disconnect();
            return;
        }

        if (_realmCatalog is null)
        {
            await _session.SendAsync(
                PacketBuilder.SendServer(),
                cancellationToken,
                "SendServer");
            return;
        }

        if (_advertisedRealms is null ||
            _selectedRealm is not null ||
            !LegacyRealmSelectionPacket.TryRead(
                packet,
                out var realmId) ||
            !_advertisedRealms.TryFind(
                realmId,
                out var advertised) ||
            advertised is null)
        {
            _session.Disconnect();
            return;
        }

        var currentRealms = await _realmCatalog.ReadEnabledAsync(
            cancellationToken);
        if (!currentRealms.TryFind(realmId, out var selected) ||
            selected is null ||
            selected != advertised ||
            (_session.IsSecure &&
             selected.RealmId !=
                _options.Game.WorldInstances.ProcessRealmId))
        {
            _session.Disconnect();
            return;
        }

        _selectedRealm = selected;
        await _session.SendAsync(
            PacketBuilder.SendServer(selected),
            cancellationToken,
            "SendServer");
    }

    private async Task SendGenericFailureAsync(
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.LoginFailed(3),
            cancellationToken,
            "LoginFailed");
    }

    private static byte[] CopyPasswordBytes(ReadOnlySpan<byte> field)
    {
        var terminator = field.IndexOf((byte)0);
        var length = terminator >= 0 ? terminator : field.Length;
        return field[..length].ToArray();
    }

    private static void ClearAvailableCredentialField(byte[] buffer)
    {
        const int credentialOffset = 36;
        if (buffer.Length <= credentialOffset)
        {
            return;
        }

        var length = Math.Min(
            AuthenticationOptions.MaximumPasswordBytes,
            buffer.Length - credentialOffset);
        CryptographicOperations.ZeroMemory(
            buffer.AsSpan(credentialOffset, length));
    }

    private static void LogReceived(GamePacket packet)
    {
        if (packet.Opcode == Opcodes.Login)
        {
            Console.WriteLine(
                $"[login] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length}");
            return;
        }

        Console.WriteLine(
            $"[login] recv {Opcodes.Name(packet.Opcode)} opcode={packet.Opcode} len={packet.Length} hex={packet.ToHexPreview(32)}");
    }
}
