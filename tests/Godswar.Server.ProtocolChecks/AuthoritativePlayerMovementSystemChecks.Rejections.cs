using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static partial class AuthoritativePlayerMovementSystemChecks
{
    private static void CheckRuntimeStateGatesAreAtomic()
    {
        var notReadySystem = CreateSystem(x: 5f, z: 6f);
        var input = Input(id: 1, x: 5.25f, z: 6f);

        var notReady = notReadySystem.ProcessLatest(
            input,
            World(isReady: false),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            notReadySystem,
            notReady,
            AuthoritativePlayerMovementRejectionReason.NotReady,
            "not-ready world");
        Check.Equal(
            1UL,
            notReady.AcknowledgedInputId,
            "not-ready correction acknowledges processed input");
        Check.Equal(
            0UL,
            notReady.Revision,
            "not-ready correction retains position revision");
        Check.Equal(
            5f,
            notReady.AuthoritativeX,
            "not-ready correction retains position");

        var resumed = notReadySystem.ProcessLatest(
            Input(id: 2, x: 5.25f, z: 6f),
            World(),
            TimeSpan.FromMilliseconds(70));
        Check.True(
            resumed.Accepted,
            "a later input can resume after not-ready correction");

        var deadSystem = CreateSystem(x: 5f, z: 6f);
        var dead = deadSystem.ProcessLatest(
            Input(id: 1, x: 5.25f, z: 6f),
            World(isAlive: false),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            deadSystem,
            dead,
            AuthoritativePlayerMovementRejectionReason.Dead,
            "dead player");
        Check.Equal(
            1UL,
            dead.AcknowledgedInputId,
            "dead correction acknowledges processed input");
        Check.Equal(
            0UL,
            dead.Revision,
            "dead correction retains position revision");
    }

    private static void CheckCoordinateAndMultiplierValidation()
    {
        var invalidInputs = new[]
        {
            Input(id: 1, x: float.NaN),
            Input(id: 1, z: float.PositiveInfinity),
            Input(id: 1, x: float.MaxValue),
            Input(id: 1, auxiliary: float.NegativeInfinity)
        };
        foreach (var input in invalidInputs)
        {
            var system = CreateSystem();
            var rejected = system.ProcessLatest(
                input,
                World(),
                TimeSpan.FromMilliseconds(50));
            AssertAtomicRejection(
                system,
                rejected,
                AuthoritativePlayerMovementRejectionReason
                    .InvalidCoordinates,
                "non-finite or unrepresentable movement coordinate");
            Check.Equal(
                1UL,
                rejected.AcknowledgedInputId,
                "invalid-coordinate correction acknowledges input");
        }

        foreach (var multiplier in new[]
                 {
                     0f,
                     -1f,
                     float.NaN,
                     4.01f
                 })
        {
            var system = CreateSystem();
            var rejected = system.ProcessLatest(
                Input(id: 1),
                World(multiplier: multiplier),
                TimeSpan.FromMilliseconds(50));
            AssertAtomicRejection(
                system,
                rejected,
                AuthoritativePlayerMovementRejectionReason.Malformed,
                "invalid server movement multiplier");
            Check.Equal(
                1UL,
                rejected.AcknowledgedInputId,
                "malformed correction acknowledges processed input");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = CreateSystem(x: float.MaxValue),
            "invalid authoritative baseline is rejected");
    }

    private static void CheckTransportAndWorldSemantics()
    {
        AssertFirstInputReason(
            Input(id: 1) with
            {
                TransportEpoch = TransportEpoch - 1
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.TransportEpoch,
            "old transport epoch");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                TransportEpoch = TransportEpoch + 1
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.TransportEpoch,
            "future transport epoch");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                WorldGeneration = WorldGeneration - 1
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "stale world generation");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                WorldGeneration = WorldGeneration + 1
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.MapTransition,
            "future world generation");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                MapId = MapId + 1
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.MapTransition,
            "wrong current map");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                SourceObjectId = SourceObjectId + 1
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.TransportSource,
            "wrong source object");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                Source =
                    AuthoritativePlayerMovementSource.Udp
            },
            World(
                allowedSources:
                    AuthoritativePlayerMovementSource.Tls),
            AuthoritativePlayerMovementRejectionReason.TransportSource,
            "disabled movement transport");
        AssertFirstInputReason(
            Input(id: 1) with
            {
                TargetsCurrentWorld = false
            },
            World(),
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "non-current-world input");

        var changedWorld = World() with
        {
            WorldGeneration = WorldGeneration + 1
        };
        AssertFirstInputReason(
            Input(id: 1),
            changedWorld,
            AuthoritativePlayerMovementRejectionReason.MapTransition,
            "server world changed before authority rebase");

        var system = CreateSystem();
        var stale = system.ProcessLatest(
            Input(id: 1) with
            {
                WorldGeneration = WorldGeneration - 1
            },
            World(),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            system,
            stale,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "stale generation is atomic");
        var current = system.ProcessLatest(
            Input(id: 1, x: 0.25f),
            World(),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            system,
            current,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "a processed stale-generation input consumes its global ID");
        var nextCurrent = system.ProcessLatest(
            Input(id: 2, x: 0.25f),
            World(),
            TimeSpan.FromMilliseconds(70));
        Check.True(
            nextCurrent.Accepted,
            "the next global input ID can target the current world");
    }

    private static void CheckExactTransportEpochAdvance()
    {
        var system = CreateSystem();
        var accepted = system.ProcessLatest(
            Input(id: 44, x: 0.5f) with
            {
                Source = AuthoritativePlayerMovementSource.Tls
            },
            World(
                allowedSources:
                    AuthoritativePlayerMovementSource.Tls),
            TimeSpan.FromMilliseconds(50));
        Check.True(
            accepted.Accepted,
            "TLS movement establishes pre-handoff authority");
        var before = system.Snapshot;

        Check.True(
            !system.TryAdvanceTransportEpoch(TransportEpoch),
            "current epoch cannot be replayed as a handoff");
        Check.True(
            !system.TryAdvanceTransportEpoch(TransportEpoch + 2),
            "transport epoch cannot jump");
        Check.Equal(
            before,
            system.Snapshot,
            "failed epoch advances are atomic");

        var nextEpoch = TransportEpoch + 1;
        Check.True(
            system.TryAdvanceTransportEpoch(nextEpoch),
            "authenticated exact-next transport epoch advances");
        var advanced = system.Snapshot;
        Check.Equal(
            nextEpoch,
            advanced.TransportEpoch,
            "snapshot publishes advanced transport epoch");
        Check.Equal(
            before.AcknowledgedInputId,
            advanced.AcknowledgedInputId,
            "transport handoff preserves global input acknowledgement");
        Check.Equal(
            before.SimulationTick,
            advanced.SimulationTick,
            "transport handoff preserves simulation tick");
        Check.Equal(
            before.Revision,
            advanced.Revision,
            "transport handoff preserves position revision");
        Check.Equal(
            before.AuthoritativeX,
            advanced.AuthoritativeX,
            "transport handoff preserves position");

        var currentWorld = World(
            allowedSources:
                AuthoritativePlayerMovementSource.Udp) with
        {
            TransportEpoch = nextEpoch
        };
        var oldEpoch = system.ProcessLatest(
            Input(id: 45, x: 0.6f),
            currentWorld,
            TimeSpan.FromMilliseconds(70));
        AssertAtomicRejection(
            system,
            oldEpoch,
            AuthoritativePlayerMovementRejectionReason.TransportEpoch,
            "old epoch is rejected after handoff");
        Check.Equal(
            before.AcknowledgedInputId,
            oldEpoch.AcknowledgedInputId,
            "old epoch cannot change global acknowledgement");

        var sameLogicalInput = system.ProcessLatest(
            Input(id: 44, x: 0.5f) with
            {
                TransportEpoch = nextEpoch
            },
            currentWorld,
            TimeSpan.FromMilliseconds(70));
        AssertAtomicRejection(
            system,
            sameLogicalInput,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "same logical input is deduplicated across handoff");
        Check.Equal(
            44UL,
            sameLogicalInput.AcknowledgedInputId,
            "cross-epoch duplicate cannot roll acknowledgement back");

        var lowerInput = system.ProcessLatest(
            Input(id: 43, x: 0.4f) with
            {
                TransportEpoch = nextEpoch
            },
            currentWorld,
            TimeSpan.FromMilliseconds(70));
        AssertAtomicRejection(
            system,
            lowerInput,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "lower input ID remains stale after handoff");
        Check.Equal(
            44UL,
            lowerInput.AcknowledgedInputId,
            "lower cross-epoch input cannot roll acknowledgement back");

        var newEpoch = system.ProcessLatest(
            Input(id: 45, x: 0.6f) with
            {
                TransportEpoch = nextEpoch
            },
            currentWorld,
            TimeSpan.FromMilliseconds(70));
        Check.True(
            newEpoch.Accepted,
            "first authenticated input in exact-next epoch is accepted");
        Check.Equal(
            45UL,
            newEpoch.AcknowledgedInputId,
            "new epoch continues the global input ID sequence");

        var exhausted = new AuthoritativePlayerMovementSystem(
            new AuthoritativePlayerMovementBaseline(
                uint.MaxValue,
                WorldGeneration,
                MapId,
                SourceObjectId,
                OpaqueState: 0,
                CurrentX: 0f,
                CurrentZ: 0f,
                Auxiliary: 0f,
                TimeSpan.Zero));
        Check.True(
            !exhausted.TryAdvanceTransportEpoch(0),
            "exhausted transport epoch cannot wrap");
    }

    private static void
        CheckWorldRehydrationPreservesSessionSequence()
    {
        var system = new AuthoritativePlayerMovementSystem(
            new AuthoritativePlayerMovementBaseline(
                TransportEpoch,
                WorldGeneration + 1,
                MapId + 1,
                SourceObjectId,
                OpaqueState: 0x0002_0000,
                CurrentX: 10f,
                CurrentZ: 20f,
                Auxiliary: 1f,
                ServerTimestamp:
                    TimeSpan.FromMilliseconds(500),
                AcknowledgedInputId: 73,
                PositionRevision: 19,
                SimulationTick: 101));
        var snapshot = system.Snapshot;
        Check.True(
            snapshot.TransportEpoch == TransportEpoch &&
            snapshot.AcknowledgedInputId == 73 &&
            snapshot.Revision == 19 &&
            snapshot.SimulationTick == 101 &&
            snapshot.WorldGeneration == WorldGeneration + 1 &&
            snapshot.MapId == MapId + 1,
            "world rehydration preserves session-global transport, acknowledgement, revision, and tick");

        var duplicate = system.ProcessLatest(
            Input(id: 73) with
            {
                WorldGeneration = WorldGeneration + 1,
                MapId = MapId + 1
            },
            World() with
            {
                WorldGeneration = WorldGeneration + 1,
                MapId = MapId + 1
            },
            TimeSpan.FromMilliseconds(520));
        AssertAtomicRejection(
            system,
            duplicate,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "rehydrated acknowledgement prevents input replay");
    }

    private static void CheckReplayAndServerTimeRules()
    {
        var system = CreateSystem();
        var first = system.ProcessLatest(
            Input(id: 1),
            World(),
            TimeSpan.FromMilliseconds(50));
        Check.True(first.Accepted, "stale-input fixture baseline");

        var duplicate = system.ProcessLatest(
            Input(id: 1, x: 0.1f),
            World(),
            TimeSpan.FromMilliseconds(100));
        AssertAtomicRejection(
            system,
            duplicate,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "duplicate input ID");
        Check.Equal(
            first.AcknowledgedInputId,
            duplicate.AcknowledgedInputId,
            "duplicate keeps the highest prior acknowledgement");

        var regressedClock = system.ProcessLatest(
            Input(id: 2, x: 0.1f),
            World(),
            TimeSpan.FromMilliseconds(49));
        AssertAtomicRejection(
            system,
            regressedClock,
            AuthoritativePlayerMovementRejectionReason.StaleInput,
            "regressed server receive timestamp");
        Check.Equal(
            first.AcknowledgedInputId,
            regressedClock.AcknowledgedInputId,
            "regressed server time keeps the prior acknowledgement");
    }

    private static void AssertFirstInputReason(
        in AuthoritativePlayerMovementInput input,
        in AuthoritativePlayerMovementWorldContext world,
        AuthoritativePlayerMovementRejectionReason reason,
        string description)
    {
        var system = CreateSystem();
        var rejected = system.ProcessLatest(
            input,
            world,
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            system,
            rejected,
            reason,
            description);
    }

    private static void AssertAtomicRejection(
        AuthoritativePlayerMovementSystem system,
        in AuthoritativePlayerMovementDecision decision,
        AuthoritativePlayerMovementRejectionReason reason,
        string description)
    {
        Check.True(
            !decision.Accepted &&
            decision.RejectionReason == reason,
            $"{description} has the expected rejection reason");
        var snapshot = system.Snapshot;
        Check.Equal(
            snapshot.AcknowledgedInputId,
            decision.AcknowledgedInputId,
            $"{description} reports authoritative acknowledgement");
        Check.Equal(
            snapshot.Revision,
            decision.Revision,
            $"{description} reports authoritative revision");
        Check.Equal(
            snapshot.AuthoritativeX,
            decision.AuthoritativeX,
            $"{description} retains authoritative X");
        Check.Equal(
            snapshot.AuthoritativeZ,
            decision.AuthoritativeZ,
            $"{description} retains authoritative Z");
    }

    private static AuthoritativePlayerMovementSystem CreateSystem(
        float x = 0f,
        float z = 0f,
        uint state = 0,
        float auxiliary = 0f) =>
        new(
            new AuthoritativePlayerMovementBaseline(
                TransportEpoch,
                WorldGeneration,
                MapId,
                SourceObjectId,
                state,
                x,
                z,
                auxiliary,
                TimeSpan.Zero));

    private static AuthoritativePlayerMovementInput Input(
        ulong id,
        float x = 0f,
        float z = 0f,
        uint state = 0,
        float auxiliary = 0f) =>
        new(
            TransportEpoch,
            id,
            WorldGeneration,
            MapId,
            state,
            x,
            z,
            auxiliary,
            SourceObjectId,
            AuthoritativePlayerMovementSource.Udp,
            TargetsCurrentWorld: true);

    private static AuthoritativePlayerMovementWorldContext World(
        bool isReady = true,
        bool isAlive = true,
        float multiplier = 1f,
        AuthoritativePlayerMovementSource allowedSources =
            AuthoritativePlayerMovementSource.Tls |
            AuthoritativePlayerMovementSource.Udp) =>
        new(
            TransportEpoch,
            WorldGeneration,
            MapId,
            SourceObjectId,
            isReady,
            isAlive,
            multiplier,
            allowedSources);
}
