using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.ProtocolChecks;

internal readonly record struct EmulatedMovementSend(
    ulong PacketIdentity,
    ulong DuplicatePacketIdentity,
    bool Dropped);

internal readonly record struct EmulatedMovementDelivery(
    ulong PacketIdentity,
    ulong LogicalInputId,
    SecureRealtimeTransportSource Source,
    TimeSpan SentAt,
    TimeSpan DeliveredAt,
    int JitterMilliseconds,
    int PayloadBytes,
    SecureRealtimeMovementInput Input);

/// <summary>
/// Deterministic, in-process impairment model. It performs no socket I/O,
/// waiting, or externally targeted traffic.
/// </summary>
internal sealed class DeterministicMovementNetwork
{
    private readonly List<ScheduledPacket> _pending = [];
    private readonly int _baseLatencyMilliseconds;
    private readonly int _jitterMilliseconds;
    private readonly TimeSpan _udpBlockedUntil;
    private readonly ulong _burstLossFirstInputId;
    private readonly ulong _burstLossLastInputId;
    private uint _randomState;
    private ulong _nextPacketIdentity = 1;

    public DeterministicMovementNetwork(
        uint seed,
        int baseLatencyMilliseconds,
        int jitterMilliseconds,
        TimeSpan udpBlockedUntil = default,
        ulong burstLossFirstInputId = 0,
        ulong burstLossLastInputId = 0)
    {
        if (seed == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seed));
        }
        if (baseLatencyMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseLatencyMilliseconds));
        }
        if (jitterMilliseconds < 0 ||
            jitterMilliseconds > baseLatencyMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(jitterMilliseconds));
        }
        if ((burstLossFirstInputId == 0) !=
            (burstLossLastInputId == 0) ||
            burstLossFirstInputId > burstLossLastInputId)
        {
            throw new ArgumentOutOfRangeException(
                nameof(burstLossFirstInputId));
        }

        _randomState = seed;
        _baseLatencyMilliseconds = baseLatencyMilliseconds;
        _jitterMilliseconds = jitterMilliseconds;
        _udpBlockedUntil = udpBlockedUntil;
        _burstLossFirstInputId = burstLossFirstInputId;
        _burstLossLastInputId = burstLossLastInputId;
    }

    public int UdpBlockedDrops { get; private set; }

    public int BurstLossDrops { get; private set; }

    public int MaximumPending { get; private set; }

    public EmulatedMovementSend Send(
        in SecureRealtimeMovementInput input,
        SecureRealtimeTransportSource source,
        TimeSpan sentAt,
        int forcedAdditionalDelayMilliseconds = 0,
        int? duplicateAdditionalDelayMilliseconds = null)
    {
        if (forcedAdditionalDelayMilliseconds < 0 ||
            duplicateAdditionalDelayMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(forcedAdditionalDelayMilliseconds));
        }

        var originalIdentity = NextPacketIdentity();
        var duplicateIdentity =
            duplicateAdditionalDelayMilliseconds.HasValue
                ? NextPacketIdentity()
                : 0;
        if (source == SecureRealtimeTransportSource.Udp &&
            sentAt < _udpBlockedUntil)
        {
            UdpBlockedDrops++;
            return new EmulatedMovementSend(
                originalIdentity,
                duplicateIdentity,
                Dropped: true);
        }

        if (_burstLossFirstInputId != 0 &&
            input.InputId >= _burstLossFirstInputId &&
            input.InputId <= _burstLossLastInputId)
        {
            BurstLossDrops++;
            return new EmulatedMovementSend(
                originalIdentity,
                duplicateIdentity,
                Dropped: true);
        }

        var payload = Encode(input, source);
        Schedule(
            originalIdentity,
            input.InputId,
            source,
            sentAt,
            forcedAdditionalDelayMilliseconds,
            payload);
        if (duplicateAdditionalDelayMilliseconds is { } duplicateDelay)
        {
            Schedule(
                duplicateIdentity,
                input.InputId,
                source,
                sentAt,
                duplicateDelay,
                payload.ToArray());
        }

        MaximumPending = Math.Max(MaximumPending, _pending.Count);
        return new EmulatedMovementSend(
            originalIdentity,
            duplicateIdentity,
            Dropped: false);
    }

    public IReadOnlyList<EmulatedMovementDelivery> Drain()
    {
        var scheduled = _pending
            .OrderBy(static packet => packet.DeliveredAt)
            .ThenBy(static packet => packet.PacketIdentity)
            .ToArray();
        _pending.Clear();

        var deliveries =
            new EmulatedMovementDelivery[scheduled.Length];
        for (var index = 0; index < scheduled.Length; index++)
        {
            var packet = scheduled[index];
            if (!SecureRealtimeMovementProtocol
                    .TryDecodeMovementInput(
                        packet.Payload,
                        packet.Source,
                        out var input))
            {
                throw new InvalidOperationException(
                    "The emulated movement packet did not decode.");
            }

            deliveries[index] = new EmulatedMovementDelivery(
                packet.PacketIdentity,
                packet.LogicalInputId,
                packet.Source,
                packet.SentAt,
                packet.DeliveredAt,
                packet.JitterMilliseconds,
                packet.Payload.Length,
                input);
        }

        return deliveries;
    }

    private static byte[] Encode(
        in SecureRealtimeMovementInput input,
        SecureRealtimeTransportSource source)
    {
        var payload =
            new byte[SecureRealtimeMovementProtocol.MovementInputBytes];
        if (!SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                source,
                payload,
                out var bytesWritten) ||
            bytesWritten != payload.Length)
        {
            throw new InvalidOperationException(
                "The emulated movement packet did not encode.");
        }

        return payload;
    }

    private void Schedule(
        ulong packetIdentity,
        ulong logicalInputId,
        SecureRealtimeTransportSource source,
        TimeSpan sentAt,
        int forcedAdditionalDelayMilliseconds,
        byte[] payload)
    {
        var jitter = NextJitterMilliseconds();
        var delay = checked(
            _baseLatencyMilliseconds +
            jitter +
            forcedAdditionalDelayMilliseconds);
        _pending.Add(
            new ScheduledPacket(
                packetIdentity,
                logicalInputId,
                source,
                sentAt,
                sentAt + TimeSpan.FromMilliseconds(delay),
                jitter,
                payload));
    }

    private int NextJitterMilliseconds()
    {
        _randomState ^= _randomState << 13;
        _randomState ^= _randomState >> 17;
        _randomState ^= _randomState << 5;
        var width = checked(_jitterMilliseconds * 2 + 1);
        return checked(
            (int)(_randomState % checked((uint)width)) -
            _jitterMilliseconds);
    }

    private ulong NextPacketIdentity() =>
        _nextPacketIdentity++;

    private sealed record ScheduledPacket(
        ulong PacketIdentity,
        ulong LogicalInputId,
        SecureRealtimeTransportSource Source,
        TimeSpan SentAt,
        TimeSpan DeliveredAt,
        int JitterMilliseconds,
        byte[] Payload);
}
