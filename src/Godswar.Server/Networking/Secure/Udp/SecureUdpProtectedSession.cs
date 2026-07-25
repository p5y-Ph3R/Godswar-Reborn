using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpProtectedSession : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _bindingSecret;
    private readonly SecureUdpConnectionKey _connectionId;
    private readonly uint _serverId;
    private readonly SecureUdpPeerRole _localRole;
    private readonly SecureUdpTrafficDirection _sendDirection;
    private readonly SecureUdpTrafficDirection _receiveDirection;
    private readonly TimeSpan _previousEpochOverlap;
    private readonly TimeProvider _timeProvider;
    private byte[] _sendKey;
    private uint _sendKeyEpoch =
        SecureUdpProtectedConstants.InitialKeyEpoch;
    private SecureUdpSequenceCounter _sendSequence;
    private ulong _packetsSentInEpoch;
    private long _sendEpochStartedTimestamp;
    private SecureUdpReceiveEpochState _receiveCurrent;
    private SecureUdpReceiveEpochState? _receivePrevious;
    private long _receivePreviousRetiredTimestamp;
    private bool _disposed;

    public SecureUdpProtectedSession(
        SecureUdpPeerRole localRole,
        ReadOnlySpan<byte> bindingSecret,
        ReadOnlySpan<byte> connectionId,
        uint serverId,
        TimeSpan previousEpochOverlap,
        TimeProvider? timeProvider = null)
    {
        if (localRole is not (
                SecureUdpPeerRole.Client or
                SecureUdpPeerRole.Server))
        {
            throw new ArgumentOutOfRangeException(nameof(localRole));
        }
        if (bindingSecret.Length !=
                SecureUdpProtectedConstants.KeyBytes ||
            SecureUdpBindingCodec.IsAllZero(bindingSecret))
        {
            throw new ArgumentException(
                "The protected UDP binding secret must be a nonzero 32-byte value.",
                nameof(bindingSecret));
        }
        if (!SecureUdpConnectionKey.TryCreate(
                connectionId,
                out _connectionId))
        {
            throw new ArgumentException(
                "The protected UDP connection ID must be a nonzero 16-byte value.",
                nameof(connectionId));
        }
        if (serverId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(serverId));
        }
        if (previousEpochOverlap < TimeSpan.FromSeconds(1) ||
            previousEpochOverlap > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(previousEpochOverlap));
        }

        _serverId = serverId;
        _localRole = localRole;
        _previousEpochOverlap = previousEpochOverlap;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sendDirection = localRole == SecureUdpPeerRole.Server
            ? SecureUdpTrafficDirection.ServerToClient
            : SecureUdpTrafficDirection.ClientToServer;
        _receiveDirection = localRole == SecureUdpPeerRole.Server
            ? SecureUdpTrafficDirection.ClientToServer
            : SecureUdpTrafficDirection.ServerToClient;
        _bindingSecret = bindingSecret.ToArray();
        _sendKey = CreateDerivedKey(
            _sendDirection,
            SecureUdpProtectedConstants.InitialKeyEpoch);
        _receiveCurrent = new SecureUdpReceiveEpochState(
            SecureUdpProtectedConstants.InitialKeyEpoch,
            CreateDerivedKey(
                _receiveDirection,
                SecureUdpProtectedConstants.InitialKeyEpoch));
        _sendEpochStartedTimestamp = _timeProvider.GetTimestamp();
    }

    public bool TryProtect(
        SecureUdpProtectedMessageType messageType,
        ReadOnlySpan<byte> payload,
        Span<byte> destination,
        out int bytesWritten,
        out SecureUdpProtectedError error)
    {
        bytesWritten = 0;
        error = SecureUdpProtectedError.InvalidPayload;
        if (!SecureUdpProtectedPayload.IsValidContent(
                messageType,
                payload))
        {
            return false;
        }
        if (!CanSend(messageType))
        {
            error = SecureUdpProtectedError.InvalidMessageDirection;
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                error = SecureUdpProtectedError.Disposed;
                return false;
            }
            if (!_sendSequence.TryPeek(out var sequence))
            {
                error = SecureUdpProtectedError.SequenceExhausted;
                return false;
            }

            var header = new SecureUdpProtectedHeader(
                _connectionId,
                _sendKeyEpoch,
                sequence,
                _receiveCurrent.GetAcknowledgement(),
                messageType,
                checked((ushort)payload.Length));
            if (!SecureUdpProtectedCodec.TryEncrypt(
                    header,
                    _sendKey,
                    payload,
                    destination,
                    out bytesWritten,
                    out error))
            {
                return false;
            }

            if (_packetsSentInEpoch != ulong.MaxValue)
            {
                _packetsSentInEpoch++;
            }
            _sendSequence.Commit();
            return true;
        }
    }

    public bool TryUnprotect(
        ReadOnlySpan<byte> datagram,
        Span<byte> plaintextDestination,
        out SecureUdpProtectedHeader header,
        out int payloadBytes,
        out SecureUdpProtectedError error)
    {
        header = default;
        payloadBytes = 0;
        error = SecureUdpProtectedError.MalformedDatagram;
        if (!SecureUdpProtectedCodec.TryDecodeHeader(
                datagram,
                out var candidateHeader))
        {
            return false;
        }
        if (candidateHeader.ConnectionId != _connectionId)
        {
            error = SecureUdpProtectedError.ConnectionMismatch;
            return false;
        }
        if (!CanReceive(candidateHeader.MessageType))
        {
            error = SecureUdpProtectedError.InvalidMessageDirection;
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                error = SecureUdpProtectedError.Disposed;
                return false;
            }

            ExpirePreviousReceiveEpoch();
            if (candidateHeader.KeyEpoch ==
                _receiveCurrent.KeyEpoch)
            {
                return TryUnprotectKnownEpoch(
                    _receiveCurrent,
                    datagram,
                    plaintextDestination,
                    out header,
                    out payloadBytes,
                    out error);
            }
            if (_receivePrevious is not null &&
                candidateHeader.KeyEpoch ==
                    _receivePrevious.KeyEpoch)
            {
                return TryUnprotectKnownEpoch(
                    _receivePrevious,
                    datagram,
                    plaintextDestination,
                    out header,
                    out payloadBytes,
                    out error);
            }
            if (_receiveCurrent.KeyEpoch == uint.MaxValue ||
                candidateHeader.KeyEpoch !=
                    _receiveCurrent.KeyEpoch + 1)
            {
                error = SecureUdpProtectedError.UnknownKeyEpoch;
                return false;
            }

            return TryUnprotectNextEpoch(
                candidateHeader.KeyEpoch,
                datagram,
                plaintextDestination,
                out header,
                out payloadBytes,
                out error);
        }
    }

    public bool TryRotateSendEpoch(
        out SecureUdpProtectedError error)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                error = SecureUdpProtectedError.Disposed;
                return false;
            }
            if (!SecureUdpSequenceRules.TryGetNextKeyEpoch(
                    _sendKeyEpoch,
                    out _))
            {
                error = SecureUdpProtectedError.EpochExhausted;
                return false;
            }

            RotateSendEpoch();
            error = SecureUdpProtectedError.None;
            return true;
        }
    }

    public SecureUdpKeyRotationStatus RotateSendEpochIfDue(
        ulong packetLimit,
        TimeSpan maximumAge)
    {
        if (packetLimit == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packetLimit));
        }
        if (maximumAge < TimeSpan.FromSeconds(1) ||
            maximumAge > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return SecureUdpKeyRotationStatus.Disposed;
            }
            var now = _timeProvider.GetTimestamp();
            var due = _packetsSentInEpoch >= packetLimit ||
                now >= _sendEpochStartedTimestamp &&
                _timeProvider.GetElapsedTime(
                    _sendEpochStartedTimestamp,
                    now) >= maximumAge;
            if (!due)
            {
                return SecureUdpKeyRotationStatus.NotDue;
            }
            if (!SecureUdpSequenceRules.TryGetNextKeyEpoch(
                    _sendKeyEpoch,
                    out _))
            {
                return SecureUdpKeyRotationStatus.EpochExhausted;
            }

            RotateSendEpoch();
            return SecureUdpKeyRotationStatus.Rotated;
        }
    }

    public SecureUdpProtectedSessionSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ExpirePreviousReceiveEpoch();
            return new SecureUdpProtectedSessionSnapshot(
                _sendKeyEpoch,
                _sendSequence.Next,
                _sendSequence.IsExhausted,
                _packetsSentInEpoch,
                _receiveCurrent.KeyEpoch,
                _receivePrevious?.KeyEpoch ?? 0,
                _receiveCurrent.HasReceived,
                _receiveCurrent.HighestSequence,
                _receiveCurrent.ReplayBitsLow,
                _receiveCurrent.ReplayBitsHigh);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_bindingSecret);
            CryptographicOperations.ZeroMemory(_sendKey);
            _receiveCurrent.Clear();
            _receivePrevious?.Clear();
            _receivePrevious = null;
            _disposed = true;
        }
    }

    private bool TryUnprotectKnownEpoch(
        SecureUdpReceiveEpochState state,
        ReadOnlySpan<byte> datagram,
        Span<byte> plaintextDestination,
        out SecureUdpProtectedHeader header,
        out int payloadBytes,
        out SecureUdpProtectedError error)
    {
        header = default;
        payloadBytes = 0;
        SecureUdpProtectedCodec.TryDecodeHeader(
            datagram,
            out var candidateHeader);
        if (!state.WouldAccept(candidateHeader.Sequence))
        {
            error = SecureUdpProtectedError.ReplayRejected;
            return false;
        }
        if (!SecureUdpProtectedCodec.TryDecrypt(
                datagram,
                state.Key,
                plaintextDestination,
                out header,
                out payloadBytes,
                out error))
        {
            return false;
        }
        if (!state.TryAccept(header.Sequence))
        {
            plaintextDestination[..payloadBytes].Clear();
            header = default;
            payloadBytes = 0;
            error = SecureUdpProtectedError.ReplayRejected;
            return false;
        }

        return true;
    }

    private bool TryUnprotectNextEpoch(
        uint keyEpoch,
        ReadOnlySpan<byte> datagram,
        Span<byte> plaintextDestination,
        out SecureUdpProtectedHeader header,
        out int payloadBytes,
        out SecureUdpProtectedError error)
    {
        Span<byte> candidateKey = stackalloc byte[
            SecureUdpProtectedConstants.KeyBytes];
        try
        {
            DeriveKey(
                _receiveDirection,
                keyEpoch,
                candidateKey);
            if (!SecureUdpProtectedCodec.TryDecrypt(
                    datagram,
                    candidateKey,
                    plaintextDestination,
                    out header,
                    out payloadBytes,
                    out error))
            {
                return false;
            }

            var next = new SecureUdpReceiveEpochState(
                keyEpoch,
                candidateKey.ToArray());
            if (!next.TryAccept(header.Sequence))
            {
                next.Clear();
                plaintextDestination[..payloadBytes].Clear();
                header = default;
                payloadBytes = 0;
                error = SecureUdpProtectedError.ReplayRejected;
                return false;
            }

            _receivePrevious?.Clear();
            _receivePrevious = _receiveCurrent;
            _receivePreviousRetiredTimestamp =
                _timeProvider.GetTimestamp();
            _receiveCurrent = next;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidateKey);
        }
    }

    private void RotateSendEpoch()
    {
        if (!SecureUdpSequenceRules.TryGetNextKeyEpoch(
                _sendKeyEpoch,
                out var nextEpoch))
        {
            throw new InvalidOperationException(
                "The protected UDP key epoch is exhausted.");
        }
        var nextKey = CreateDerivedKey(_sendDirection, nextEpoch);
        CryptographicOperations.ZeroMemory(_sendKey);
        _sendKey = nextKey;
        _sendKeyEpoch = nextEpoch;
        _sendSequence.Reset();
        _packetsSentInEpoch = 0;
        _sendEpochStartedTimestamp = _timeProvider.GetTimestamp();
    }

    private void ExpirePreviousReceiveEpoch()
    {
        if (_receivePrevious is null)
        {
            return;
        }

        var now = _timeProvider.GetTimestamp();
        if (now < _receivePreviousRetiredTimestamp ||
            _timeProvider.GetElapsedTime(
                _receivePreviousRetiredTimestamp,
                now) < _previousEpochOverlap)
        {
            return;
        }

        _receivePrevious.Clear();
        _receivePrevious = null;
        _receivePreviousRetiredTimestamp = 0;
    }

    private byte[] CreateDerivedKey(
        SecureUdpTrafficDirection direction,
        uint keyEpoch)
    {
        var key = new byte[SecureUdpProtectedConstants.KeyBytes];
        try
        {
            DeriveKey(direction, keyEpoch, key);
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    private void DeriveKey(
        SecureUdpTrafficDirection direction,
        uint keyEpoch,
        Span<byte> key)
    {
        Span<byte> connectionId = stackalloc byte[
            SecureUdpProtectedConstants.ConnectionIdBytes];
        _connectionId.WriteTo(connectionId);
        try
        {
            if (!SecureUdpTrafficKeyDerivation.TryDeriveKey(
                    _bindingSecret,
                    connectionId,
                    _serverId,
                    direction,
                    keyEpoch,
                    key))
            {
                throw new CryptographicException(
                    "Unable to derive protected UDP traffic key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(connectionId);
        }
    }

    private bool CanSend(SecureUdpProtectedMessageType messageType)
    {
        return _localRole == SecureUdpPeerRole.Client
            ? messageType == SecureUdpProtectedMessageType.Ping
            : messageType is SecureUdpProtectedMessageType.Pong or
                SecureUdpProtectedMessageType.BindingConfirm;
    }

    private bool CanReceive(SecureUdpProtectedMessageType messageType)
    {
        return _localRole == SecureUdpPeerRole.Server
            ? messageType == SecureUdpProtectedMessageType.Ping
            : messageType is SecureUdpProtectedMessageType.Pong or
                SecureUdpProtectedMessageType.BindingConfirm;
    }
}
