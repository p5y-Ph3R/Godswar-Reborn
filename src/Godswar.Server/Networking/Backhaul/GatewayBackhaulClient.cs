using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Godswar.Server.Networking.Backhaul;

internal sealed class BackhaulAdmissionRejectedException :
    IOException
{
    public BackhaulAdmissionRejectedException(
        BackhaulAdmissionStatus status)
        : base($"Worker rejected backhaul admission with {status}.")
    {
        Status = status;
    }

    public BackhaulAdmissionStatus Status { get; }
}

internal static class GatewayBackhaulClient
{
    public static async Task<GatewayBackhaulConnection> ConnectAsync(
        IPEndPoint workerEndpoint,
        string workerTlsHost,
        X509Certificate2 gatewayCertificate,
        BackhaulCertificatePins allowedWorkerCertificates,
        BackhaulHandshakeGate handshakeGate,
        GatewayWorldAdmission admission,
        BackhaulRuntimeLimits limits,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workerEndpoint);
        ArgumentNullException.ThrowIfNull(gatewayCertificate);
        ArgumentNullException.ThrowIfNull(allowedWorkerCertificates);
        ArgumentNullException.ThrowIfNull(handshakeGate);
        ArgumentNullException.ThrowIfNull(admission);
        limits.Validate();
        var clock = timeProvider ?? TimeProvider.System;
        var client = new TcpClient(
            workerEndpoint.AddressFamily);
        SslStream? tls = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = 16 * 1024;
            client.SendBufferSize = 16 * 1024;
            await ConnectWithDeadlineAsync(
                client,
                workerEndpoint,
                limits.ConnectTimeout,
                clock,
                cancellationToken);
            tls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false);
            using (await handshakeGate.AcquireAsync(
                       limits.TlsHandshakeTimeout,
                       clock,
                       cancellationToken))
            {
                await BackhaulStreamIo.AuthenticateAsGatewayAsync(
                    tls,
                    BackhaulTlsPolicy.CreateGatewayClientOptions(
                        workerTlsHost,
                        gatewayCertificate,
                        allowedWorkerCertificates,
                        clock),
                    limits.TlsHandshakeTimeout,
                    clock,
                    cancellationToken);
            }

            var openBytes = new byte[
                BackhaulProtocolConstants.OpenSessionFrameBytes];
            var responseBytes = new byte[
                BackhaulProtocolConstants.AdmissionResponseFrameBytes];
            try
            {
                if (!BackhaulCodec.TryEncodeOpenSession(
                        admission,
                        openBytes,
                        out var written) ||
                    written != openBytes.Length)
                {
                    throw new InvalidOperationException(
                        "The canonical open-session frame could not be " +
                        "encoded.");
                }

                await BackhaulStreamIo.WriteExactlyAsync(
                    tls,
                    openBytes,
                    limits.OpenSessionTimeout,
                    clock,
                    cancellationToken,
                    BackhaulTimeoutStage.OpenSessionWrite);
                await BackhaulStreamIo.ReadExactlyAsync(
                    tls,
                    responseBytes,
                    limits.OpenSessionTimeout,
                    clock,
                    cancellationToken,
                    BackhaulTimeoutStage.AdmissionResponseRead);
                if (!BackhaulCodec.TryDecodeAdmissionResponse(
                        responseBytes,
                        out var response,
                        out _))
                {
                    throw new InvalidDataException(
                        "Worker returned a malformed admission response.");
                }
                if (response.ConnectionId != admission.ConnectionId)
                {
                    throw new InvalidDataException(
                        "Worker admission response connection identity " +
                        "does not match the request.");
                }
                if (!response.IsAccepted)
                {
                    throw new BackhaulAdmissionRejectedException(
                        response.Status);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(openBytes);
                CryptographicOperations.ZeroMemory(responseBytes);
            }

            var result = new GatewayBackhaulConnection(
                client,
                tls,
                admission,
                limits,
                clock);
            tls = null;
            client = null!;
            return result;
        }
        catch
        {
            tls?.Dispose();
            client?.Dispose();
            throw;
        }
    }

    private static async Task ConnectWithDeadlineAsync(
        TcpClient client,
        IPEndPoint endpoint,
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(
            timeout,
            timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        try
        {
            await client.ConnectAsync(
                endpoint,
                lifetime.Token);
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new BackhaulTimeoutException(
                BackhaulTimeoutStage.Connect);
        }
    }
}

internal sealed class GatewayBackhaulConnection :
    IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly BackhaulRuntimeLimits _limits;
    private readonly SslStream _stream;
    private readonly TimeProvider _timeProvider;
    private int _disconnectStarted;
    private int _disposed;

    internal GatewayBackhaulConnection(
        TcpClient client,
        SslStream stream,
        GatewayWorldAdmission admission,
        BackhaulRuntimeLimits limits,
        TimeProvider timeProvider)
    {
        _client = client;
        _stream = stream;
        Admission = admission;
        _limits = limits;
        _timeProvider = timeProvider;
    }

    public GatewayWorldAdmission Admission { get; }

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

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnectStarted, 1) == 0)
        {
            _client.Dispose();
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
