using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure;

internal sealed class TlsMuxLegacyTransportFactory :
    ILegacyByteTransportFactory
{
    private readonly IReadOnlySet<string> _allowedOriginSha256;
    private readonly SslStreamCertificateContext _certificateContext;
    private readonly TlsHandshakeGate _handshakeGate;
    private readonly NetworkRuntimeOptions _options;
    private readonly TimeProvider _timeProvider;

    public TlsMuxLegacyTransportFactory(
        SecureNetworkOptions secureOptions,
        NetworkRuntimeOptions runtimeOptions,
        SslStreamCertificateContext certificateContext,
        TlsHandshakeGate handshakeGate,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secureOptions);
        _options = runtimeOptions
            ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _certificateContext = certificateContext
            ?? throw new ArgumentNullException(nameof(certificateContext));
        _handshakeGate = handshakeGate
            ?? throw new ArgumentNullException(nameof(handshakeGate));
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
            await ValidatePrefaceAsync(
                sslStream,
                endpointRole,
                secureRole,
                cancellationToken);

            if (endpointRole == NetworkEndpointRole.Game)
            {
                await RejectGameUntilTicketSliceAsync(
                    sslStream,
                    cancellationToken);
                throw new SecureTransportException(
                    "Secure game transport is unavailable before ticket binding is implemented.");
            }

            var transport = new TlsMuxLegacyTransport(
                connectionOwner,
                abortConnection,
                sslStream,
                remoteEndPoint,
                endpointRole,
                secureRole,
                _options,
                _timeProvider);
            sslStream = null;
            return transport;
        }
        catch
        {
            if (sslStream is not null)
            {
                await sslStream.DisposeAsync();
            }
            TryClose(abortConnection);
            TryDispose(connectionOwner);
            throw;
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

    private async Task ValidatePrefaceAsync(
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
            out _);
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
    }

    private async Task RejectGameUntilTicketSliceAsync(
        SslStream sslStream,
        CancellationToken cancellationToken)
    {
        using var bindDeadline = new CancellationTokenSource(
            _options.GameBindTimeout,
            _timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            bindDeadline.Token);
        var headerBytes = new byte[SecureProtocolConstants.FrameHeaderBytes];
        try
        {
            await ReadExactlyUnderBindDeadlineAsync(
                sslStream,
                headerBytes,
                lifetime.Token);
        }
        catch (OperationCanceledException)
            when (bindDeadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            NetworkRuntimeMetrics.RecordTimeout(
                NetworkEndpointRole.Game,
                NetworkTimeoutStage.GameBind);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.GameBind);
        }

        if (!SecureFrameCodec.TryDecodeHeader(
                headerBytes,
                SecureEndpointRole.Game,
                SecureFrameDirection.ClientToServer,
                expectedSequence: 1,
                out var header) ||
            header.Type != SecureFrameType.GameBind)
        {
            SecureNetworkMetrics.FrameCompleted(
                NetworkEndpointRole.Game,
                SecureFrameOutcome.WrongPhase);
            throw new SecureTransportException(
                "The first secure game frame must be a game-ticket bind.");
        }

        var bindBytes = new byte[SecureProtocolConstants.GameBindBytes];
        try
        {
            await ReadExactlyUnderBindDeadlineAsync(
                sslStream,
                bindBytes,
                lifetime.Token);
            if (!SecureGameControlCodec.TryDecodeBind(
                    bindBytes,
                    out var bind))
            {
                SecureNetworkMetrics.FrameCompleted(
                    NetworkEndpointRole.Game,
                    SecureFrameOutcome.Malformed);
                throw new SecureTransportException(
                    "The secure game bind payload is malformed.");
            }
            bind!.Dispose();
        }
        catch (OperationCanceledException)
            when (bindDeadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            NetworkRuntimeMetrics.RecordTimeout(
                NetworkEndpointRole.Game,
                NetworkTimeoutStage.GameBind);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.GameBind);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bindBytes);
        }

        SecureNetworkMetrics.FrameCompleted(
            NetworkEndpointRole.Game,
            SecureFrameOutcome.WrongPhase);
        var payload = new byte[SecureProtocolConstants.BindResultBytes];
        if (!SecureGameControlCodec.TryEncodeBindResult(
                new SecureBindResult(SecureBindStatus.PolicyRejected),
                payload,
                out _))
        {
            throw new InvalidOperationException(
                "The policy-rejected game bind result could not be encoded.");
        }

        var response = new byte[
            SecureProtocolConstants.FrameHeaderBytes +
            SecureProtocolConstants.BindResultBytes];
        if (!SecureFrameCodec.TryEncode(
                new SecureFrameHeader(
                    SecureProtocolConstants.BindResultBytes,
                    SecureFrameType.BindResult,
                    Sequence: 1),
                payload,
                SecureEndpointRole.Game,
                SecureFrameDirection.ServerToClient,
                response,
                out _))
        {
            throw new InvalidOperationException(
                "The policy-rejected game bind frame could not be encoded.");
        }

        await SecureStreamIo.WriteExactlyAsync(
            sslStream,
            response,
            _options.ReliableWriteTimeout,
            _timeProvider,
            cancellationToken,
            NetworkTimeoutStage.ReliableWrite);
    }

    private void RecordHandshakeBeforeAdmission(
        NetworkEndpointRole role,
        SecureHandshakeOutcome outcome,
        TimeSpan duration)
    {
        SecureNetworkMetrics.HandshakeRejectedBeforeAdmission(
            role,
            outcome,
            duration);
    }

    private static async ValueTask ReadExactlyUnderBindDeadlineAsync(
        SslStream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(
                destination[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"TLS peer closed after {offset} of {destination.Length} game-bind bytes.");
            }

            offset += read;
        }
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
