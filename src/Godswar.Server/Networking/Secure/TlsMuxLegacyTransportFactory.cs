using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.Operations;

namespace Godswar.Server.Networking.Secure;

internal sealed partial class TlsMuxLegacyTransportFactory :
    ILegacyByteTransportFactory
{
    private readonly IReadOnlySet<string> _allowedOriginSha256;
    private readonly SslStreamCertificateContext _certificateContext;
    private readonly SecureGameTarget? _gameTarget;
    private readonly TlsHandshakeGate _handshakeGate;
    private readonly NetworkRuntimeOptions _options;
    private readonly IGameTicketStore? _ticketStore;
    private readonly TimeProvider _timeProvider;
    private readonly SecureUdpSessionAuthority? _udpSessionAuthority;
    private readonly ushort _udpPort;

    public TlsMuxLegacyTransportFactory(
        SecureNetworkOptions secureOptions,
        NetworkRuntimeOptions runtimeOptions,
        SslStreamCertificateContext certificateContext,
        TlsHandshakeGate handshakeGate,
        TimeProvider? timeProvider = null,
        IGameTicketStore? ticketStore = null,
        SecureGameTarget? gameTarget = null,
        SecureUdpSessionAuthority? udpSessionAuthority = null)
    {
        ArgumentNullException.ThrowIfNull(secureOptions);
        _options = runtimeOptions
            ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _certificateContext = certificateContext
            ?? throw new ArgumentNullException(nameof(certificateContext));
        _handshakeGate = handshakeGate
            ?? throw new ArgumentNullException(nameof(handshakeGate));
        if ((ticketStore is null) != (gameTarget is null))
        {
            throw new ArgumentException(
                "The secure ticket store and game target must be configured together.");
        }
        if (udpSessionAuthority is not null && gameTarget is null)
        {
            throw new ArgumentException(
                "The UDP session authority requires the authenticated game-ticket target.");
        }
        _ticketStore = ticketStore;
        _gameTarget = gameTarget;
        _udpSessionAuthority = udpSessionAuthority;
        _udpPort = checked((ushort)secureOptions.Udp.Port);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options.Validate();
        SecureNetworkOptions.ValidateSecureRuntime(_options);
        _allowedOriginSha256 = secureOptions.BuildAllowedHashSet();
    }

    public async ValueTask<ILegacyByteTransport> CreateAsync(
        TcpClient client,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            client.NoDelay = true;
            var remoteEndPoint =
                client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            return await CreateCoreAsync(
                client.GetStream(),
                client,
                client.Close,
                remoteEndPoint,
                endpointRole,
                acceptedTimestamp,
                cancellationToken);
        }
        catch
        {
            TryDispose(client);
            throw;
        }
    }

    internal async ValueTask<ILegacyByteTransport> CreateForStreamAsync(
        Stream connectionStream,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectionStream);
        return await CreateCoreAsync(
            connectionStream,
            connectionStream,
            connectionStream.Dispose,
            "secure-test",
            endpointRole,
            acceptedTimestamp,
            cancellationToken);
    }

    private async ValueTask<ILegacyByteTransport> CreateCoreAsync(
        Stream connectionStream,
        IDisposable connectionOwner,
        Action abortConnection,
        string remoteEndPoint,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken)
    {
        var secureRole = ToSecureRole(endpointRole);
        SslStream? sslStream = null;
        TlsMuxLegacyTransport? transport = null;
        SecureUdpSessionLease? pendingUdpLease = null;
        SecureUdpBindingGrant? udpGrant = null;
        try
        {
            sslStream = new SslStream(
                connectionStream,
                leaveInnerStreamOpen: false);
            await AuthenticateAsync(
                sslStream,
                endpointRole,
                acceptedTimestamp,
                cancellationToken);
            var connectionContext = await ValidatePrefaceAsync(
                sslStream,
                endpointRole,
                secureRole,
                cancellationToken);

            SecureBoundGamePrincipal? gamePrincipal = null;
            if (endpointRole == NetworkEndpointRole.Game)
            {
                if (_ticketStore is null || _gameTarget is null)
                {
                    await RejectGameUntilTicketSliceAsync(
                        sslStream,
                        cancellationToken);
                    throw new SecureTransportException(
                        "Secure game transport is unavailable without a ticket authority.");
                }

                gamePrincipal = await BindGameAsync(
                    sslStream,
                    connectionContext,
                    _ticketStore,
                    _gameTarget,
                    cancellationToken);
            }

            if (gamePrincipal is not null &&
                _udpSessionAuthority is not null &&
                _gameTarget is not null)
            {
                var registration = _udpSessionAuthority.Register(
                    connectionContext,
                    gamePrincipal);
                if (registration.IsRegistered)
                {
                    pendingUdpLease = registration.Lease!;
                    udpGrant = CreateUdpBindingGrant(
                        pendingUdpLease,
                        _gameTarget.ServerId);
                }
            }

            transport = new TlsMuxLegacyTransport(
                connectionOwner,
                abortConnection,
                sslStream,
                remoteEndPoint,
                endpointRole,
                secureRole,
                _options,
                _timeProvider,
                connectionContext,
                gamePrincipal,
                pendingUdpLease);
            pendingUdpLease = null;
            sslStream = null;
            if (udpGrant is not null)
            {
                await transport.SendUdpBindingGrantAsync(
                    udpGrant,
                    cancellationToken);
            }
            var result = transport;
            transport = null;
            return result;
        }
        catch
        {
            pendingUdpLease?.Dispose();
            if (transport is not null)
            {
                await transport.DisposeAsync();
            }
            else
            {
                if (sslStream is not null)
                {
                    await sslStream.DisposeAsync();
                }
                TryClose(abortConnection);
                TryDispose(connectionOwner);
            }
            throw;
        }
        finally
        {
            udpGrant?.Dispose();
        }
    }

    private SecureUdpBindingGrant CreateUdpBindingGrant(
        SecureUdpSessionLease lease,
        uint serverId)
    {
        Span<byte> connectionId = stackalloc byte[
            SecureUdpBindingConstants.ConnectionIdBytes];
        Span<byte> proofKey = stackalloc byte[
            SecureUdpTlsProofAuthenticator.KeyBytes];
        try
        {
            if (!lease.TryCopyGrantMaterial(
                    connectionId,
                    proofKey,
                    out var expiryUnixMilliseconds))
            {
                throw new SecureTransportException(
                    "The registered UDP binding material expired before TLS delivery.");
            }

            return new SecureUdpBindingGrant(
                _udpPort,
                serverId,
                expiryUnixMilliseconds,
                connectionId,
                proofKey,
                lease.Capabilities);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
            CryptographicOperations.ZeroMemory(proofKey);
        }
    }

    private static void TryClose(Action close)
    {
        try
        {
            close();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryDispose(IDisposable owner)
    {
        try
        {
            owner.Dispose();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task AuthenticateAsync(
        SslStream sslStream,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken)
    {
        var elapsed = _timeProvider.GetElapsedTime(acceptedTimestamp);
        var remaining = _options.TlsHandshakeTimeout - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            RecordHandshakeBeforeAdmission(
                endpointRole,
                SecureHandshakeOutcome.DeadlineExceeded,
                elapsed);
            NetworkRuntimeMetrics.RecordTimeout(
                endpointRole,
                NetworkTimeoutStage.TlsHandshake);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.TlsHandshake);
        }

        using var deadline = new CancellationTokenSource(
            remaining,
            _timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        IDisposable? gateLease = null;
        var handshakeStarted = false;
        var outcome = SecureHandshakeOutcome.Cancelled;
        try
        {
            try
            {
                gateLease = await _handshakeGate.AcquireAsync(lifetime.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested)
            {
                RecordHandshakeBeforeAdmission(
                    endpointRole,
                    SecureHandshakeOutcome.DeadlineExceeded,
                    _timeProvider.GetElapsedTime(acceptedTimestamp));
                NetworkRuntimeMetrics.RecordTimeout(
                    endpointRole,
                    NetworkTimeoutStage.TlsHandshake);
                throw new NetworkDeadlineException(
                    NetworkTimeoutStage.TlsHandshake);
            }

            SecureNetworkMetrics.HandshakeStarted(endpointRole);
            handshakeStarted = true;
            await sslStream.AuthenticateAsServerAsync(
                SecureTlsPolicy.CreateServerOptions(_certificateContext),
                lifetime.Token);
            if (!SecureTlsPolicy.IsNegotiationAccepted(sslStream))
            {
                outcome = SecureHandshakeOutcome.PolicyRejected;
                throw new SecureTransportException(
                    "The TLS negotiation did not satisfy the required protocol, ALPN, confidentiality, integrity, and cipher-suite policy.");
            }

            outcome = SecureHandshakeOutcome.Accepted;
            ControlledHostPrivacyEvidence.RecordIfActive(
                ControlledHostEvidenceEvent.TlsPolicyAccepted);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            outcome = SecureHandshakeOutcome.DeadlineExceeded;
            NetworkRuntimeMetrics.RecordTimeout(
                endpointRole,
                NetworkTimeoutStage.TlsHandshake);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.TlsHandshake);
        }
        catch (AuthenticationException error)
        {
            outcome = SecureHandshakeOutcome.AuthenticationFailed;
            throw new SecureTransportException(
                "TLS authentication failed.",
                error);
        }
        finally
        {
            if (handshakeStarted)
            {
                SecureNetworkMetrics.HandshakeCompleted(
                    endpointRole,
                    outcome,
                    _timeProvider.GetElapsedTime(acceptedTimestamp));
            }
            gateLease?.Dispose();
        }
    }

    private async Task<SecureConnectionContext> ValidatePrefaceAsync(
        SslStream sslStream,
        NetworkEndpointRole endpointRole,
        SecureEndpointRole secureRole,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[SecureProtocolConstants.ClientPrefaceBytes];
        try
        {
            await SecureStreamIo.ReadExactlyAsync(
                sslStream,
                bytes,
                _options.SecurePrefaceTimeout,
                _timeProvider,
                cancellationToken,
                NetworkTimeoutStage.SecurePreface);
        }
        catch (NetworkDeadlineException)
        {
            SecureNetworkMetrics.PrefaceCompleted(
                endpointRole,
                SecurePrefaceOutcome.DeadlineExceeded);
            NetworkRuntimeMetrics.RecordTimeout(
                endpointRole,
                NetworkTimeoutStage.SecurePreface);
            throw;
        }

        var outcome = SecurePrefacePolicy.Evaluate(
            bytes,
            secureRole,
            _allowedOriginSha256,
            out var preface);
        SecureNetworkMetrics.PrefaceCompleted(endpointRole, outcome);
        var connectionId = new byte[
            SecureProtocolConstants.ConnectionIdBytes];
        if (outcome == SecurePrefaceOutcome.Accepted)
        {
            FillNonzero(connectionId);
        }

        var response = new byte[SecureProtocolConstants.ServerPrefaceBytes];
        var serverPreface = new SecureServerPreface(
            SecurePrefacePolicy.ToServerStatus(outcome),
            secureRole,
            connectionId);
        if (!SecurePrefaceCodec.TryEncodeServer(
                serverPreface,
                response,
                out var written) ||
            written != response.Length)
        {
            throw new InvalidOperationException(
                "The canonical secure server preface could not be encoded.");
        }

        await SecureStreamIo.WriteExactlyAsync(
            sslStream,
            response,
            _options.ReliableWriteTimeout,
            _timeProvider,
            cancellationToken,
            NetworkTimeoutStage.ReliableWrite);
        if (outcome != SecurePrefaceOutcome.Accepted)
        {
            throw new SecureTransportException(
                $"Secure client preface rejected with finite outcome '{outcome.ToMetricTag()}'.");
        }
        ControlledHostPrivacyEvidence.RecordIfActive(
            ControlledHostEvidenceEvent
                .AcceptedSecurePrefaceResponseWritten);

        return new SecureConnectionContext(
            preface!.Role,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            connectionId,
            preface.ClientInstanceId.Span,
            preface.OriginSha256.Span);
    }

    private static SecureEndpointRole ToSecureRole(
        NetworkEndpointRole endpointRole)
    {
        return endpointRole switch
        {
            NetworkEndpointRole.Login => SecureEndpointRole.Login,
            NetworkEndpointRole.Game => SecureEndpointRole.Game,
            _ => throw new ArgumentOutOfRangeException(nameof(endpointRole))
        };
    }

    private static void FillNonzero(Span<byte> destination)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RandomNumberGenerator.Fill(destination);
            if (!SecureProtocolValidation.IsAllZero(destination))
            {
                return;
            }
        }

        throw new CryptographicException(
            "CSPRNG returned an invalid TLS connection ID.");
    }
}
