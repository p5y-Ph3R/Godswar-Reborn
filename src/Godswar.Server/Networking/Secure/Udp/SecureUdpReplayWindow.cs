namespace Godswar.Server.Networking.Secure.Udp;

internal struct SecureUdpReplayWindow
{
    private bool _initialized;
    private ulong _highestSequence;
    private ulong _bitsLow;
    private ulong _bitsHigh;

    public bool IsInitialized => _initialized;

    public ulong HighestSequence => _highestSequence;

    public ulong BitsLow => _bitsLow;

    public ulong BitsHigh => _bitsHigh;

    public bool WouldAccept(ulong sequence)
    {
        if (!_initialized || sequence > _highestSequence)
        {
            return true;
        }

        var distance = _highestSequence - sequence;
        if (distance >= SecureUdpProtectedConstants.ReplayWindowBits)
        {
            return false;
        }

        return distance < 64
            ? (_bitsLow & (1UL << checked((int)distance))) == 0
            : (_bitsHigh &
                (1UL << checked((int)(distance - 64)))) == 0;
    }

    public bool TryAccept(ulong sequence)
    {
        if (!WouldAccept(sequence))
        {
            return false;
        }

        if (!_initialized)
        {
            _initialized = true;
            _highestSequence = sequence;
            _bitsLow = 1;
            _bitsHigh = 0;
            return true;
        }

        if (sequence > _highestSequence)
        {
            ShiftForNewHighest(sequence - _highestSequence);
            _highestSequence = sequence;
            _bitsLow |= 1;
            return true;
        }

        var distance = _highestSequence - sequence;
        if (distance < 64)
        {
            _bitsLow |= 1UL << checked((int)distance);
        }
        else
        {
            _bitsHigh |= 1UL << checked((int)(distance - 64));
        }
        return true;
    }

    public SecureUdpAcknowledgement ToAcknowledgement(uint keyEpoch)
    {
        if (!_initialized || keyEpoch == 0)
        {
            return SecureUdpAcknowledgement.None;
        }

        var previousMask = (_bitsLow >> 1) | (_bitsHigh << 63);
        return new SecureUdpAcknowledgement(
            keyEpoch,
            _highestSequence,
            previousMask);
    }

    private void ShiftForNewHighest(ulong distance)
    {
        if (distance >= SecureUdpProtectedConstants.ReplayWindowBits)
        {
            _bitsLow = 0;
            _bitsHigh = 0;
            return;
        }
        if (distance >= 64)
        {
            _bitsHigh = _bitsLow << checked((int)(distance - 64));
            _bitsLow = 0;
            return;
        }

        var shift = checked((int)distance);
        _bitsHigh = (_bitsHigh << shift) |
            (_bitsLow >> (64 - shift));
        _bitsLow <<= shift;
    }
}
