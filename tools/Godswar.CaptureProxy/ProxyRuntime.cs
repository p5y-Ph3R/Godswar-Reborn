sealed class ProxyState(string? defaultGameHost, int defaultGamePort)
{
    private readonly TaskCompletionSource<(string Host, int Port)> _gameTarget =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetGameTarget(string host, int port)
    {
        _gameTarget.TrySetResult((host, port));
    }

    public async ValueTask<(string Host, int Port)> WaitForGameTargetAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(defaultGameHost))
        {
            return (defaultGameHost, defaultGamePort);
        }

        return await _gameTarget.Task.WaitAsync(cancellationToken);
    }
}

sealed class PacketFrameAccumulator
{
    private const int MaxFrameLength = 64 * 1024;

    private readonly List<byte> _clearPending = [];
    private readonly List<byte> _rawPending = [];
    private long _pendingStreamOffset;

    public IReadOnlyList<CapturedPacketFrame> Append(ReadOnlySpan<byte> clearChunk, ReadOnlySpan<byte> rawChunk)
    {
        if (clearChunk.Length != rawChunk.Length)
        {
            throw new InvalidOperationException("Clear and raw packet buffers must have the same length.");
        }

        _clearPending.AddRange(clearChunk.ToArray());
        _rawPending.AddRange(rawChunk.ToArray());

        var packetIndex = 0;
        var packets = new List<CapturedPacketFrame>();

        while (_clearPending.Count >= 4)
        {
            var declaredLength = _clearPending[0] | (_clearPending[1] << 8);
            if (declaredLength < 4 || declaredLength > MaxFrameLength)
            {
                packets.Add(CreateFrame(packetIndex++, _clearPending.Count, declaredLength, null, "invalid frame length"));
                _pendingStreamOffset += _clearPending.Count;
                _clearPending.Clear();
                _rawPending.Clear();
                break;
            }

            if (_clearPending.Count < declaredLength)
            {
                break;
            }

            var opcode = _clearPending[2] | (_clearPending[3] << 8);
            packets.Add(CreateFrame(packetIndex++, declaredLength, declaredLength, opcode, string.Empty));
            _pendingStreamOffset += declaredLength;
            _clearPending.RemoveRange(0, declaredLength);
            _rawPending.RemoveRange(0, declaredLength);
        }

        return packets;
    }

    private CapturedPacketFrame CreateFrame(
        int packetIndex,
        int actualLength,
        int? declaredLength,
        int? opcode,
        string notes)
    {
        return new CapturedPacketFrame(
            PacketIndex: packetIndex,
            StreamOffset: _pendingStreamOffset,
            DeclaredLength: declaredLength,
            ActualLength: actualLength,
            Opcode: opcode,
            ClearBytes: _clearPending.GetRange(0, actualLength).ToArray(),
            RawBytes: _rawPending.GetRange(0, actualLength).ToArray(),
            Notes: notes);
    }
}
