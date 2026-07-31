using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.Networking.Backhaul;

internal sealed class WorkerBackhaulAdmissionException :
    IOException
{
    public WorkerBackhaulAdmissionException(
        BackhaulAdmissionStatus status)
        : base($"Worker backhaul admission ended with {status}.")
    {
        Status = status;
    }

    public BackhaulAdmissionStatus Status { get; }
}

/// <summary>
/// Worker-side transport factory. It consumes mTLS and the fixed admission
/// preface before exposing only the subsequent legacy encrypted byte stream
/// to ClientSession.
/// </summary>
internal sealed class WorkerBackhaulTransportFactory :
    ILegacyByteTransportFactory
{
    private readonly BackhaulCertificatePins _allowedGateways;
    private readonly BackhaulHandshakeGate _handshakeGate;
    private readonly BackhaulRuntimeLimits _limits;
    private readonly WorkerBackhaulAdmissionRegistry _registry;
    private readonly TimeProvider _timeProvider;
    private readonly X509Certificate2 _workerCertificate;

    public WorkerBackhaulTransportFactory(
        X509Certificate2 workerCertificate,
        BackhaulCertificatePins allowedGateways,
        BackhaulHandshakeGate handshakeGate,
        WorkerBackhaulAdmissionRegistry registry,
        BackhaulRuntimeLimits limits,
        TimeProvider? timeProvider = null)
    {
        _workerCertificate = workerCertificate ??
            throw new ArgumentNullException(
                nameof(workerCertificate));
        _allowedGateways = allowedGateways ??
            throw new ArgumentNullException(
                nameof(allowedGateways));
        _handshakeGate = handshakeGate ??
            throw new ArgumentNullException(
                nameof(handshakeGate));
        _registry = registry ??
            throw new ArgumentNullException(nameof(registry));
        _limits = limits.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        BackhaulTlsPolicy.ValidateLocalCertificate(
            workerCertificate,
            BackhaulCertificatePurpose.WorkerServer,
            _timeProvider);
    }

    public async ValueTask<ILegacyByteTransport> CreateAsync(
        TcpClient client,
        NetworkEndpointRole endpointRole,
        long acceptedTimestamp,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        _ = acceptedTimestamp;
        if (endpointRole != NetworkEndpointRole.Game)
        {
            client.Dispose();
            throw new InvalidDataException(
                "Worker backhaul accepts only game endpoint sessions.");
        }

        SslStream? tls = null;
        WorkerBackhaulAdmissionLease? admissionLease = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = 16 * 1024;
            client.SendBufferSize = 16 * 1024;
            tls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false);
            using (await _handshakeGate.AcquireAsync(
                       _limits.TlsHandshakeTimeout,
                       _timeProvider,
                       cancellationToken))
            {
                await BackhaulStreamIo.AuthenticateAsWorkerAsync(
                    tls,
                    BackhaulTlsPolicy.CreateWorkerServerOptions(
                        _workerCertificate,
                        _allowedGateways,
                        _timeProvider),
                    _limits.TlsHandshakeTimeout,
                    _timeProvider,
                    cancellationToken);
            }

            var openBytes = new byte[
                BackhaulProtocolConstants.OpenSessionFrameBytes];
            try
            {
                await BackhaulStreamIo.ReadExactlyAsync(
                    tls,
                    openBytes,
                    _limits.OpenSessionTimeout,
                    _timeProvider,
                    cancellationToken,
                    BackhaulTimeoutStage.WorkerOpenSessionRead);
                if (!BackhaulCodec.TryDecodeOpenSession(
                        openBytes,
                        out var admission,
                        out var failure))
                {
                    var status = FailureStatus(failure);
                    await TryWriteResponseAsync(
                        tls,
                        new BackhaulAdmissionResponse(
                            status,
                            Guid.Empty),
                        cancellationToken);
                    throw new WorkerBackhaulAdmissionException(status);
                }

                var admissionStatus = _registry.TryReserve(
                    admission!,
                    out admissionLease);
                if (admissionStatus !=
                        BackhaulAdmissionStatus.Accepted ||
                    admissionLease is null)
                {
                    await TryWriteResponseAsync(
                        tls,
                        new BackhaulAdmissionResponse(
                            admissionStatus,
                            admission!.ConnectionId),
                        cancellationToken);
                    throw new WorkerBackhaulAdmissionException(
                        admissionStatus);
                }
                if (!admissionLease.Activate())
                {
                    await TryWriteResponseAsync(
                        tls,
                        new BackhaulAdmissionResponse(
                            BackhaulAdmissionStatus.PolicyRejected,
                            admission!.ConnectionId),
                        cancellationToken);
                    throw new WorkerBackhaulAdmissionException(
                        BackhaulAdmissionStatus.PolicyRejected);
                }

                await WriteResponseAsync(
                    tls,
                    new BackhaulAdmissionResponse(
                        BackhaulAdmissionStatus.Accepted,
                        admission!.ConnectionId),
                    cancellationToken);

                var transport =
                    new WorkerBackhaulLegacyTransport(
                        client,
                        tls,
                        admissionLease,
                        _limits,
                        _timeProvider);
                tls = null;
                admissionLease = null;
                client = null!;
                return transport;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(openBytes);
            }
        }
        catch
        {
            admissionLease?.Dispose();
            tls?.Dispose();
            client?.Dispose();
            throw;
        }
    }

    private async Task TryWriteResponseAsync(
        SslStream stream,
        BackhaulAdmissionResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(
                stream,
                response,
                cancellationToken);
        }
        catch (Exception error)
            when (error is IOException or
                BackhaulTimeoutException or
                OperationCanceledException)
        {
            // The finite local rejection remains authoritative even when
            // the untrusted peer cannot receive it.
        }
    }

    private async Task WriteResponseAsync(
        SslStream stream,
        BackhaulAdmissionResponse response,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[
            BackhaulProtocolConstants.AdmissionResponseFrameBytes];
        try
        {
            if (!BackhaulCodec.TryEncodeAdmissionResponse(
                    response,
                    bytes,
                    out var written) ||
                written != bytes.Length)
            {
                throw new InvalidOperationException(
                    "The canonical backhaul response could not be encoded.");
            }

            await BackhaulStreamIo.WriteExactlyAsync(
                stream,
                bytes,
                _limits.OpenSessionTimeout,
                _timeProvider,
                cancellationToken,
                BackhaulTimeoutStage.WorkerAdmissionResponseWrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static BackhaulAdmissionStatus FailureStatus(
        BackhaulDecodeFailure failure) =>
        failure switch
        {
            BackhaulDecodeFailure.UnsupportedVersion =>
                BackhaulAdmissionStatus.VersionRejected,
            BackhaulDecodeFailure.InvalidAdmission =>
                BackhaulAdmissionStatus.PolicyRejected,
            _ => BackhaulAdmissionStatus.Malformed
        };
}

internal sealed class WorkerBackhaulLegacyTransport :
    ILegacyByteTransport,
    IAuthenticatedGameTransport
{
    private readonly TcpClient _client;
    private readonly WorkerBackhaulAdmissionLease _lease;
    private readonly BackhaulRuntimeLimits _limits;
    private readonly SslStream _stream;
    private readonly TimeProvider _timeProvider;
    private int _authenticated;
    private int _disconnectStarted;
    private int _disposed;

    internal WorkerBackhaulLegacyTransport(
        TcpClient client,
        SslStream stream,
        WorkerBackhaulAdmissionLease lease,
        BackhaulRuntimeLimits limits,
        TimeProvider timeProvider)
    {
        _client = client;
        _stream = stream;
        _lease = lease;
        _limits = limits;
        _timeProvider = timeProvider;
        WorldAdmission = lease.Admission;
        BoundGamePrincipal =
            WorldAdmission.CreatePrincipal();
    }

    public string RemoteEndPoint => "authenticated-gateway";

    public SecureBoundGamePrincipal BoundGamePrincipal { get; }

    public GatewayWorldAdmission WorldAdmission { get; }

    internal bool IsMarkedAuthenticated =>
        Volatile.Read(ref _authenticated) != 0;

    public ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _stream.ReadAsync(
            destination,
            cancellationToken);
    }

    public ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return BackhaulStreamIo.WriteExactlyAsync(
            _stream,
            source,
            _limits.WriteTimeout,
            _timeProvider,
            cancellationToken,
            BackhaulTimeoutStage.TransportWrite);
    }

    public void MarkAuthenticated()
    {
        ThrowIfDisposed();
        if (!_lease.IsActive)
        {
            throw new InvalidOperationException(
                "A released backhaul admission cannot be authenticated.");
        }

        Volatile.Write(ref _authenticated, 1);
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnectStarted, 1) == 0)
        {
            try
            {
                _client.Dispose();
            }
            finally
            {
                _lease.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _stream.DisposeAsync();
        }
        finally
        {
            _client.Dispose();
            _lease.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _disconnectStarted) != 0,
            this);
    }
}
