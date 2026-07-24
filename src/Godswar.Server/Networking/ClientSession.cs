using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Networking;

internal sealed class ClientSession : IAsyncDisposable
{
    private readonly NetworkEndpointRole _endpointRole;
    private readonly BoundedReliableEgress _egress;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action? _markAuthenticated;
    private readonly NetworkRuntimeOptions _options;
    private readonly ILegacyByteTransport _transport;
    private readonly PacketCipher _receiveCipher = new();
    private readonly PacketCipher _sendCipher = new();
    private readonly TimeProvider _timeProvider;
    private bool _hasReceivedPacket;
    private int _disconnected;

    public ClientSession(
        ILegacyByteTransport transport,
        NetworkRuntimeOptions? options = null,
        NetworkEndpointRole endpointRole = NetworkEndpointRole.Game,
        TimeProvider? timeProvider = null,
        Action? markAuthenticated = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new NetworkRuntimeOptions();
        _options.Validate();
        _endpointRole = endpointRole;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _markAuthenticated = markAuthenticated;
        _egress = new BoundedReliableEgress(
            _options,
            endpointRole,
            WriteEncryptedAsync,
            DisconnectTransport,
            _timeProvider);
    }

    public string RemoteEndPoint => _transport.RemoteEndPoint;

    public void MarkAuthenticated()
    {
        _markAuthenticated?.Invoke();
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0)
        {
            return;
        }

        CancelLifetime();
        _egress.Abort(new OperationCanceledException("Session disconnected."));
        DisconnectTransport();
    }

    public async Task<GamePacket?> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var isFirstPacket = !_hasReceivedPacket;
        using var firstPacketDeadline = isFirstPacket
            ? new CancellationTokenSource(
                _options.FirstPacketTimeout,
                _timeProvider)
            : null;
        using var firstPacketLifetime = isFirstPacket
            ? CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token,
                firstPacketDeadline!.Token)
            : null;
        var effectiveToken = firstPacketLifetime?.Token ?? cancellationToken;

        try
        {
            return await ReadPacketCoreAsync(isFirstPacket, effectiveToken);
        }
        catch (OperationCanceledException)
            when (firstPacketDeadline?.IsCancellationRequested == true
                && !cancellationToken.IsCancellationRequested
                && !_lifetime.IsCancellationRequested)
        {
            DeadlineExceeded(NetworkTimeoutStage.FirstPacket);
            throw new NetworkDeadlineException(
                NetworkTimeoutStage.FirstPacket);
        }
    }

    private async Task<GamePacket?> ReadPacketCoreAsync(
        bool isFirstPacket,
        CancellationToken cancellationToken)
    {
        var firstByteStage = isFirstPacket
            ? NetworkTimeoutStage.FirstPacket
            : NetworkTimeoutStage.Idle;
        var firstByteTimeout = isFirstPacket
            ? _options.FirstPacketTimeout
            : _options.IdleTimeout;
        var firstByte = await ReadFirstByteAsync(
            firstByteStage,
            firstByteTimeout,
            cancellationToken);
        if (firstByte is null)
        {
            return null;
        }

        var header = new byte[2];
        header[0] = firstByte.Value;
        await ReadRemainingAsync(
            header.AsMemory(1),
            NetworkTimeoutStage.PacketHeader,
            _options.PacketHeaderTimeout,
            cancellationToken,
            allowEofBeforeAnyByte: false);
        _receiveCipher.Transform(header);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        if (length < 4 || length > LegacyProtocolLimits.MaxPacketLength)
        {
            Disconnect();
            throw new InvalidDataException($"Invalid packet length {length}.");
        }

        var rest = new byte[length - 2];
        if (!await ReadRemainingAsync(
            rest,
            NetworkTimeoutStage.PacketBody,
            _options.PacketBodyTimeout,
            cancellationToken,
            allowEofBeforeAnyByte: true))
        {
            return null;
        }
        _receiveCipher.Transform(rest);

        var packet = new byte[length];
        header.CopyTo(packet.AsSpan(0, 2));
        rest.CopyTo(packet.AsSpan(2));
        _hasReceivedPacket = true;
        return new GamePacket(packet);
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> clearPacket,
        CancellationToken cancellationToken,
        string? label = null,
        bool framed = true)
    {
        if (!string.IsNullOrWhiteSpace(label))
        {
            LogSend(clearPacket, label, framed);
        }

        await _egress.WriteAsync(clearPacket, cancellationToken);
    }

    private void LogSend(ReadOnlyMemory<byte> clearPacketMemory, string label, bool framed)
    {
        var clearPacket = clearPacketMemory.Span;
        var previewLength = ShouldLogFullPacket(label)
            ? clearPacket.Length
            : Math.Min(clearPacket.Length, 32);
        var hexPreview = Convert.ToHexString(clearPacket[..previewLength]);

        if (clearPacket.Length < 4)
        {
            Console.WriteLine($"[net] send {label} to {RemoteEndPoint} actual={clearPacket.Length} hex={hexPreview}");
            return;
        }

        if (!framed)
        {
            Console.WriteLine($"[net] send {label} to {RemoteEndPoint} stream-chunk actual={clearPacket.Length} hex={hexPreview}");
            return;
        }

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(clearPacket[..2]);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(clearPacket.Slice(2, 2));
        var mismatch = framed && declaredLength != clearPacket.Length ? " declared/actual-mismatch" : string.Empty;
        Console.WriteLine(
            $"[net] send {label} to {RemoteEndPoint} opcode={opcode} declared={declaredLength} actual={clearPacket.Length}{mismatch} hex={hexPreview}");
    }

    private static bool ShouldLogFullPacket(string label)
    {
        return label.Contains("VisiblePlayer", StringComparison.Ordinal)
            || label.Contains("PlayerInspectEquipment", StringComparison.Ordinal)
            || label.Contains("PlayerInspectClear", StringComparison.Ordinal)
            || label.Contains("PlayerInspectVisual", StringComparison.Ordinal);
    }

    private async Task<byte?> ReadFirstByteAsync(
        NetworkTimeoutStage stage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await ReadWithDeadlineAsync(
            buffer,
            stage,
            timeout,
            cancellationToken);
        return read == 0 ? null : buffer[0];
    }

    private async Task<bool> ReadRemainingAsync(
        Memory<byte> destination,
        NetworkTimeoutStage stage,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowEofBeforeAnyByte)
    {
        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token,
            deadline.Token);
        var offset = 0;
        try
        {
            while (offset < destination.Length)
            {
                var read = await _transport.ReadAsync(
                    destination[offset..],
                    readLifetime.Token);
                NetworkRuntimeMetrics.RecordTransportBytes(
                    _endpointRole,
                    NetworkTrafficDirection.Inbound,
                    read);
                if (read == 0)
                {
                    if (offset == 0 && allowEofBeforeAnyByte)
                    {
                        return false;
                    }

                    throw new EndOfStreamException("Socket closed mid-packet.");
                }

                offset += read;
            }

            return true;
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !_lifetime.IsCancellationRequested)
        {
            DeadlineExceeded(stage);
            throw new NetworkDeadlineException(stage);
        }
    }

    private async Task<int> ReadWithDeadlineAsync(
        Memory<byte> destination,
        NetworkTimeoutStage stage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token,
            deadline.Token);
        try
        {
            var read = await _transport.ReadAsync(
                destination,
                readLifetime.Token);
            NetworkRuntimeMetrics.RecordTransportBytes(
                _endpointRole,
                NetworkTrafficDirection.Inbound,
                read);
            return read;
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested
                && !_lifetime.IsCancellationRequested)
        {
            DeadlineExceeded(stage);
            throw new NetworkDeadlineException(stage);
        }
    }

    private ValueTask WriteEncryptedAsync(
        ReadOnlyMemory<byte> clearBytes,
        CancellationToken cancellationToken)
    {
        var encrypted = clearBytes.ToArray();
        _sendCipher.Transform(encrypted);
        return WriteTransportAsync(encrypted, cancellationToken);
    }

    private async ValueTask WriteTransportAsync(
        ReadOnlyMemory<byte> encrypted,
        CancellationToken cancellationToken)
    {
        await _transport.WriteAsync(encrypted, cancellationToken);
        NetworkRuntimeMetrics.RecordTransportBytes(
            _endpointRole,
            NetworkTrafficDirection.Outbound,
            encrypted.Length);
    }

    private void DeadlineExceeded(NetworkTimeoutStage stage)
    {
        NetworkRuntimeMetrics.RecordTimeout(_endpointRole, stage);
        if (Interlocked.Exchange(ref _disconnected, 1) == 0)
        {
            CancelLifetime();
            _egress.Abort(new NetworkDeadlineException(stage));
            DisconnectTransport();
        }
    }

    private void DisconnectTransport()
    {
        _transport.Disconnect();
    }

    private void CancelLifetime()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _egress.DisposeAsync();
        }
        finally
        {
            Interlocked.Exchange(ref _disconnected, 1);
            CancelLifetime();
            _lifetime.Dispose();
            await _transport.DisposeAsync();
        }
    }
}
