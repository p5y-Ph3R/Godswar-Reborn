using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.ProtocolChecks;

internal static class Phase5RealtimeDecoderFuzzChecks
{
    private const int RandomCases = 20_000;
    private const int MaximumProbeBytes = 128;

    public static Task RunAsync()
    {
        CheckTruncatedAndExtendedPackets();
        CheckSingleBitMutations();
        CheckSeededRandomPackets();
        return Task.CompletedTask;
    }

    private static void CheckTruncatedAndExtendedPackets()
    {
        var movement = EncodeMovement();
        var snapshot = EncodeSnapshot();

        for (var length = 0; length <= MaximumProbeBytes; length++)
        {
            var movementProbe = new byte[length];
            movement
                .AsSpan(0, Math.Min(length, movement.Length))
                .CopyTo(movementProbe);
            var movementAccepted =
                SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                    movementProbe,
                    SecureRealtimeTransportSource.Udp,
                    out _);
            Check.Equal(
                length == SecureRealtimeMovementProtocol.MovementInputBytes,
                movementAccepted,
                "movement decoder accepts only its exact bounded length");

            var snapshotProbe = new byte[length];
            snapshot
                .AsSpan(0, Math.Min(length, snapshot.Length))
                .CopyTo(snapshotProbe);
            var snapshotAccepted =
                SecureRealtimeMovementProtocol.TryDecodePositionSnapshot(
                    snapshotProbe,
                    out _);
            Check.Equal(
                length == SecureRealtimeMovementProtocol.PositionSnapshotBytes,
                snapshotAccepted,
                "snapshot decoder accepts only its exact bounded length");
        }
    }

    private static void CheckSingleBitMutations()
    {
        MutateEveryBit(
            EncodeMovement(),
            static payload =>
            {
                if (SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                        payload,
                        SecureRealtimeTransportSource.Udp,
                        out var decoded))
                {
                    Check.True(
                        SecureRealtimeMovementProtocol.IsValid(
                            decoded,
                            SecureRealtimeTransportSource.Udp),
                        "accepted movement mutation remains valid");
                }
            });
        MutateEveryBit(
            EncodeSnapshot(),
            static payload =>
            {
                if (SecureRealtimeMovementProtocol
                    .TryDecodePositionSnapshot(
                        payload,
                        out var decoded))
                {
                    Check.True(
                        SecureRealtimeMovementProtocol.IsValid(decoded),
                        "accepted snapshot mutation remains valid");
                }
            });
    }

    private static void CheckSeededRandomPackets()
    {
        var random = new Random(0x5A17_2026);
        var buffer = new byte[MaximumProbeBytes];
        var movementAccepted = 0;
        var snapshotsAccepted = 0;

        for (var iteration = 0; iteration < RandomCases; iteration++)
        {
            random.NextBytes(buffer);
            var length = random.Next(MaximumProbeBytes + 1);
            var payload = buffer.AsSpan(0, length);
            var transportSource = iteration % 2 == 0
                ? SecureRealtimeTransportSource.Tls
                : SecureRealtimeTransportSource.Udp;

            if (SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                    payload,
                    transportSource,
                    out var movement))
            {
                movementAccepted++;
                Check.True(
                    SecureRealtimeMovementProtocol.IsValid(
                        movement,
                        transportSource) &&
                    float.IsFinite(movement.X) &&
                    float.IsFinite(movement.Z) &&
                    float.IsFinite(movement.Auxiliary),
                    "random accepted movement has finite coordinates");
            }

            if (SecureRealtimeMovementProtocol.TryDecodePositionSnapshot(
                    payload,
                    out var snapshot))
            {
                snapshotsAccepted++;
                Check.True(
                    SecureRealtimeMovementProtocol.IsValid(snapshot) &&
                    float.IsFinite(snapshot.X) &&
                    float.IsFinite(snapshot.Z) &&
                    float.IsFinite(snapshot.Auxiliary),
                    "random accepted snapshot has finite coordinates");
            }
        }

        Check.True(
            movementAccepted <= RandomCases &&
            snapshotsAccepted <= RandomCases,
            "seeded decoder fuzz remains within its hard case budget");
    }

    private static void MutateEveryBit(
        byte[] baseline,
        Action<byte[]> probe)
    {
        for (var offset = 0; offset < baseline.Length; offset++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                var mutated = baseline.ToArray();
                mutated[offset] ^= checked((byte)(1 << bit));
                probe(mutated);
            }
        }
    }

    private static byte[] EncodeMovement()
    {
        var payload = new byte[
            SecureRealtimeMovementProtocol.MovementInputBytes];
        var input = new SecureRealtimeMovementInput(
            SecureRealtimeMovementFlags.None,
            TransportEpoch: 1,
            InputId: 1,
            ClientMonotonicMilliseconds: 50,
            WorldGeneration: 1,
            LegacyState: 0x0002_0000,
            X: 0.1f,
            Z: 0.2f,
            Auxiliary: 1f,
            MapId: 0);
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                SecureRealtimeTransportSource.Udp,
                payload,
                out var written) &&
            written == payload.Length,
            "movement fuzz fixture encodes");
        return payload;
    }

    private static byte[] EncodeSnapshot()
    {
        var payload = new byte[
            SecureRealtimeMovementProtocol.PositionSnapshotBytes];
        var snapshot = new SecureRealtimePositionSnapshot(
            SecureRealtimeSnapshotFlags.Keyframe,
            TransportEpoch: 1,
            AcknowledgedInputId: 1,
            ServerTick: 1,
            PositionRevision: 1,
            SnapshotSequence: 1,
            WorldGeneration: 1,
            LegacyState: 0x0002_0000,
            X: 0.1f,
            Z: 0.2f,
            Auxiliary: 1f,
            MapId: 0,
            SecureRealtimeMovementRejection.None);
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodePositionSnapshot(
                snapshot,
                payload,
                out var written) &&
            written == payload.Length,
            "snapshot fuzz fixture encodes");
        return payload;
    }
}
