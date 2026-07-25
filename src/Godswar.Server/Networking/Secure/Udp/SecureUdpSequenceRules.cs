namespace Godswar.Server.Networking.Secure.Udp;

internal struct SecureUdpSequenceCounter
{
    private ulong _next;
    private bool _exhausted;

    public SecureUdpSequenceCounter(ulong initialSequence)
    {
        _next = initialSequence;
        _exhausted = false;
    }

    public ulong Next => _next;

    public bool IsExhausted => _exhausted;

    public bool TryPeek(out ulong sequence)
    {
        sequence = _next;
        return !_exhausted;
    }

    public void Commit()
    {
        if (_exhausted)
        {
            throw new InvalidOperationException(
                "The protected UDP sequence is exhausted.");
        }

        if (_next == ulong.MaxValue)
        {
            _exhausted = true;
            return;
        }

        _next++;
    }

    public void Reset()
    {
        _next = 0;
        _exhausted = false;
    }
}

internal static class SecureUdpSequenceRules
{
    public static bool TryGetNextKeyEpoch(
        uint current,
        out uint next)
    {
        if (current is 0 or uint.MaxValue)
        {
            next = 0;
            return false;
        }

        next = current + 1;
        return true;
    }
}
