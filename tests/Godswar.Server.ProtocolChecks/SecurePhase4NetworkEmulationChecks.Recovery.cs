using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecurePhase4NetworkEmulationChecks
{
    private static void CheckPeriodicKeyframeRecoveryAndNoRollback()
    {
        var authority = CreateAuthority();
        var encodedSnapshots = new Dictionary<ulong, byte[]>();
        AuthoritativePlayerMovementDecision finalDecision = default;
        for (ulong inputId = 1; inputId <= 20; inputId++)
        {
            var wireInput = WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId,
                x: inputId * 0.2f);
            finalDecision = authority.ProcessLatest(
                new AuthoritativePlayerMovementInput(
                    wireInput.TransportEpoch,
                    wireInput.InputId,
                    wireInput.WorldGeneration,
                    wireInput.MapId,
                    wireInput.LegacyState,
                    wireInput.X,
                    wireInput.Z,
                    wireInput.Auxiliary,
                    SourceObjectId,
                    AuthoritativePlayerMovementSource.Udp,
                    TargetsCurrentWorld: true),
                World(
                    epoch: 1,
                    AuthoritativePlayerMovementSource.Udp),
                TimeSpan.FromMilliseconds(
                    checked((long)inputId * 50)));
            Check.True(
                finalDecision.Accepted,
                "keyframe fixture movement is authoritative");

            var snapshot = new SecureRealtimePositionSnapshot(
                inputId % 5 == 0
                    ? SecureRealtimeSnapshotFlags.Keyframe
                    : SecureRealtimeSnapshotFlags.None,
                finalDecision.TransportEpoch,
                finalDecision.AcknowledgedInputId,
                finalDecision.SimulationTick,
                finalDecision.Revision,
                SnapshotSequence: inputId,
                finalDecision.WorldGeneration,
                finalDecision.OpaqueState,
                finalDecision.AuthoritativeX,
                finalDecision.AuthoritativeZ,
                finalDecision.AuthoritativeAuxiliary,
                finalDecision.MapId,
                SecureRealtimeMovementRejection.None);
            var encoded =
                new byte[
                    SecureRealtimeMovementProtocol
                        .PositionSnapshotBytes];
            Check.True(
                SecureRealtimeMovementProtocol
                    .TryEncodePositionSnapshot(
                        snapshot,
                        encoded,
                        out var bytesWritten) &&
                bytesWritten == encoded.Length,
                "authoritative snapshot encodes at exact size");
            encodedSnapshots.Add(inputId, encoded);
        }

        // 6-9 are a burst loss. 13 arrives before 12, and the second 15 is
        // a physical duplicate. Keyframes 10 and 15 restore convergence.
        ulong[] deliveryPlan =
        [
            1, 2, 3, 4, 5,
            10, 11, 13, 12, 14,
            15, 15, 16, 17, 18, 19, 20
        ];
        var client = new SnapshotConvergenceClient();
        foreach (var sequence in deliveryPlan)
        {
            client.Offer(encodedSnapshots[sequence]);
        }

        Check.Equal(
            2,
            client.KeyframeRecoveries,
            "periodic keyframes recover both loss and reordering gaps");
        Check.True(
            client.StaleOrDuplicateDiscards >= 2,
            "reordered and duplicate snapshots are discarded");
        Check.True(
            client.GapDiscards >= 2,
            "deltas are withheld while a keyframe is required");
        Check.True(
            !client.NeedsKeyframe &&
            client.AppliedSequence == 20 &&
            client.AppliedRevision == finalDecision.Revision &&
            client.AcknowledgedInputId ==
                finalDecision.AcknowledgedInputId &&
            client.X == finalDecision.AuthoritativeX &&
            client.Z == finalDecision.AuthoritativeZ,
            "periodic keyframe stream converges to authority");
        Check.True(
            client.AppliedPositionsNeverDecreased,
            "reordering and duplication never roll position back");
    }

    private static SecureRealtimeMovementInput WireInput(
        SecureRealtimeTransportSource source,
        uint epoch,
        ulong inputId,
        float x = 0f,
        float z = 0f,
        uint legacyState = 0,
        float auxiliary = 0f) =>
        new(
            source == SecureRealtimeTransportSource.Tls
                ? SecureRealtimeMovementFlags.CurrentWorld
                : SecureRealtimeMovementFlags.None,
            epoch,
            inputId,
            ClientMonotonicMilliseconds:
                checked(inputId * 50 + 1),
            WorldGeneration,
            legacyState,
            x,
            z,
            auxiliary,
            MapId);

    private static AuthoritativePlayerMovementSystem CreateAuthority() =>
        new(
            new AuthoritativePlayerMovementBaseline(
                TransportEpoch: 1,
                WorldGeneration,
                MapId,
                SourceObjectId,
                OpaqueState: 0,
                CurrentX: 0f,
                CurrentZ: 0f,
                Auxiliary: 0f,
                ServerTimestamp: TimeSpan.Zero));

    private static AuthoritativePlayerMovementInput AuthorityInput(
        in EmulatedMovementDelivery delivery) =>
        new(
            delivery.Input.TransportEpoch,
            delivery.Input.InputId,
            delivery.Input.WorldGeneration,
            delivery.Input.MapId,
            delivery.Input.LegacyState,
            delivery.Input.X,
            delivery.Input.Z,
            delivery.Input.Auxiliary,
            SourceObjectId,
            delivery.Source == SecureRealtimeTransportSource.Tls
                ? AuthoritativePlayerMovementSource.Tls
                : AuthoritativePlayerMovementSource.Udp,
            TargetsCurrentWorld: true);

    private static AuthoritativePlayerMovementWorldContext World(
        uint epoch,
        AuthoritativePlayerMovementSource source) =>
        new(
            epoch,
            WorldGeneration,
            MapId,
            SourceObjectId,
            IsReady: true,
            IsAlive: true,
            MovementMultiplier: 1f,
            AllowedSources: source);

    private sealed class SnapshotConvergenceClient
    {
        private bool _hasAppliedSnapshot;

        public ulong HighestReceivedSequence { get; private set; }

        public ulong AppliedSequence { get; private set; }

        public ulong AppliedRevision { get; private set; }

        public ulong AcknowledgedInputId { get; private set; }

        public float X { get; private set; }

        public float Z { get; private set; }

        public bool NeedsKeyframe { get; private set; }

        public int KeyframeRecoveries { get; private set; }

        public int StaleOrDuplicateDiscards { get; private set; }

        public int GapDiscards { get; private set; }

        public bool AppliedPositionsNeverDecreased { get; private set; } =
            true;

        public bool Offer(ReadOnlySpan<byte> payload)
        {
            if (!SecureRealtimeMovementProtocol
                    .TryDecodePositionSnapshot(
                        payload,
                        out var snapshot))
            {
                throw new InvalidOperationException(
                    "The emulated snapshot did not decode.");
            }

            if (snapshot.SnapshotSequence <= HighestReceivedSequence)
            {
                StaleOrDuplicateDiscards++;
                return false;
            }

            HighestReceivedSequence = snapshot.SnapshotSequence;
            var isKeyframe =
                (snapshot.Flags &
                    SecureRealtimeSnapshotFlags.Keyframe) != 0;
            var hasGap =
                _hasAppliedSnapshot &&
                snapshot.SnapshotSequence != AppliedSequence + 1;
            if (!isKeyframe && (NeedsKeyframe || hasGap))
            {
                NeedsKeyframe = true;
                GapDiscards++;
                return false;
            }

            if (isKeyframe && (NeedsKeyframe || hasGap))
            {
                KeyframeRecoveries++;
            }

            if (_hasAppliedSnapshot &&
                (snapshot.PositionRevision < AppliedRevision ||
                 snapshot.X < X))
            {
                AppliedPositionsNeverDecreased = false;
                return false;
            }

            _hasAppliedSnapshot = true;
            NeedsKeyframe = false;
            AppliedSequence = snapshot.SnapshotSequence;
            AppliedRevision = snapshot.PositionRevision;
            AcknowledgedInputId = snapshot.AcknowledgedInputId;
            X = snapshot.X;
            Z = snapshot.Z;
            return true;
        }
    }
}
