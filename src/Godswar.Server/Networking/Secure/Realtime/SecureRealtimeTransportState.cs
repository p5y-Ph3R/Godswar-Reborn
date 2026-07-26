namespace Godswar.Server.Networking.Secure.Realtime;

internal enum SecureRealtimeReconciliationStatus : byte
{
    Accepted = 1,
    TransportChanged = 2,
    Duplicate = 3,
    TransportChangedDuplicate = 4,
    StaleInput = 5,
    TransportEpochRejected = 6,
    TransportSourceRejected = 7
}

internal readonly record struct SecureRealtimeReconciliationResult(
    SecureRealtimeReconciliationStatus Status,
    uint CurrentTransportEpoch,
    SecureRealtimeTransportSource CurrentTransportSource,
    ulong HighestInputId)
{
    public bool ShouldEnqueue =>
        Status is SecureRealtimeReconciliationStatus.Accepted or
            SecureRealtimeReconciliationStatus.TransportChanged or
            SecureRealtimeReconciliationStatus
                .TransportChangedDuplicate;
}

internal readonly record struct SecureRealtimeTransportSnapshot(
    bool HasTransport,
    uint TransportEpoch,
    SecureRealtimeTransportSource TransportSource,
    ulong HighestInputId,
    uint TransportChanges);

internal sealed class SecureRealtimeTransportState
{
    private readonly object _gate = new();
    private bool _hasTransport;
    private uint _transportEpoch;
    private SecureRealtimeTransportSource _transportSource;
    private ulong _highestInputId;
    private uint _transportChanges;

    public SecureRealtimeReconciliationResult Reconcile(
        SecureRealtimeTransportSource source,
        uint transportEpoch,
        ulong inputId)
    {
        if (source is not (
                SecureRealtimeTransportSource.Tls or
                SecureRealtimeTransportSource.Udp))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
        if (transportEpoch == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transportEpoch));
        }
        if (inputId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputId));
        }

        lock (_gate)
        {
            if (!_hasTransport)
            {
                var isLostUdpFallback =
                    source == SecureRealtimeTransportSource.Tls &&
                    transportEpoch == 2;
                if (transportEpoch != 1 && !isLostUdpFallback)
                {
                    return Result(
                        SecureRealtimeReconciliationStatus
                            .TransportEpochRejected);
                }

                _hasTransport = true;
                _transportEpoch = transportEpoch;
                _transportSource = source;
                _highestInputId = inputId;
                _transportChanges =
                    isLostUdpFallback ? 1u : 0u;
                return Result(
                    SecureRealtimeReconciliationStatus.Accepted);
            }

            var transportChanged = false;
            if (transportEpoch == _transportEpoch)
            {
                if (source != _transportSource)
                {
                    return Result(
                        SecureRealtimeReconciliationStatus
                            .TransportSourceRejected);
                }
            }
            else
            {
                if (_transportEpoch == uint.MaxValue ||
                    transportEpoch != _transportEpoch + 1)
                {
                    return Result(
                        SecureRealtimeReconciliationStatus
                            .TransportEpochRejected);
                }
                if (source == _transportSource)
                {
                    return Result(
                        SecureRealtimeReconciliationStatus
                            .TransportSourceRejected);
                }
                if (_transportChanges >= 1)
                {
                    return Result(
                        SecureRealtimeReconciliationStatus
                            .TransportEpochRejected);
                }
                if (_transportSource !=
                        SecureRealtimeTransportSource.Udp ||
                    source != SecureRealtimeTransportSource.Tls)
                {
                    return Result(
                        SecureRealtimeReconciliationStatus
                            .TransportSourceRejected);
                }

                transportChanged = true;
            }

            if (inputId < _highestInputId)
            {
                return Result(
                    SecureRealtimeReconciliationStatus.StaleInput);
            }
            if (inputId == _highestInputId)
            {
                if (transportChanged)
                {
                    _transportEpoch = transportEpoch;
                    _transportSource = source;
                    _transportChanges++;
                }
                return Result(
                    transportChanged
                        ? SecureRealtimeReconciliationStatus
                            .TransportChangedDuplicate
                        : SecureRealtimeReconciliationStatus
                            .Duplicate);
            }

            if (transportChanged)
            {
                _transportEpoch = transportEpoch;
                _transportSource = source;
                _transportChanges++;
            }
            _highestInputId = inputId;
            return Result(
                transportChanged
                    ? SecureRealtimeReconciliationStatus
                        .TransportChanged
                    : SecureRealtimeReconciliationStatus.Accepted);
        }
    }

    public SecureRealtimeTransportSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new SecureRealtimeTransportSnapshot(
                _hasTransport,
                _transportEpoch,
                _transportSource,
                _highestInputId,
                _transportChanges);
        }
    }

    private SecureRealtimeReconciliationResult Result(
        SecureRealtimeReconciliationStatus status)
    {
        return new SecureRealtimeReconciliationResult(
            status,
            _transportEpoch,
            _transportSource,
            _highestInputId);
    }
}
