using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static partial class AuthoritativePlayerMovementSystemChecks
{
    private const uint TransportEpoch = 3;
    private const uint WorldGeneration = 17;
    private const byte MapId = 2;
    private const uint SourceObjectId = 0x1448;

    public static Task RunAsync()
    {
        CheckLockedContractAndDefaults();
        CheckBaselineAndDecisionProjection();
        CheckSpeedBoundaries();
        CheckDistanceCreditCap();
        CheckMountedMultiplier();
        CheckCadenceAndEmptyTicks();
        CheckRuntimeStateGatesAreAtomic();
        CheckCoordinateAndMultiplierValidation();
        CheckTransportAndWorldSemantics();
        CheckExactTransportEpochAdvance();
        CheckWorldRehydrationPreservesSessionSequence();
        CheckReplayAndServerTimeRules();
        return Task.CompletedTask;
    }

    private static void CheckLockedContractAndDefaults()
    {
        Check.Equal(
            20,
            AuthoritativePlayerMovementPolicy
                .SimulationTicksPerSecond,
            "authoritative movement tick rate");
        Check.Equal(
            TimeSpan.FromMilliseconds(50),
            AuthoritativePlayerMovementPolicy.FixedStep,
            "authoritative movement fixed step");

        var policy = new AuthoritativePlayerMovementPolicy();
        Check.Equal(
            8f,
            policy.BaseMaximumSpeed,
            "captured base maximum speed");
        Check.Equal(
            0.75f,
            policy.PositionTolerance,
            "captured position tolerance");
        Check.Equal(
            TimeSpan.FromSeconds(1),
            policy.ElapsedCreditCap,
            "elapsed movement-credit cap");
        Check.Equal(
            TimeSpan.FromMilliseconds(20),
            policy.MinimumInputCadence,
            "minimum movement-input cadence");

        var reasons = new[]
        {
            (AuthoritativePlayerMovementRejectionReason.None, 0),
            (AuthoritativePlayerMovementRejectionReason.Malformed, 1),
            (AuthoritativePlayerMovementRejectionReason.NotReady, 2),
            (AuthoritativePlayerMovementRejectionReason.Dead, 3),
            (
                AuthoritativePlayerMovementRejectionReason
                    .InvalidCoordinates,
                4),
            (
                AuthoritativePlayerMovementRejectionReason
                    .MapTransition,
                5),
            (AuthoritativePlayerMovementRejectionReason.Cadence, 6),
            (AuthoritativePlayerMovementRejectionReason.Speed, 7),
            (AuthoritativePlayerMovementRejectionReason.Distance, 8),
            (
                AuthoritativePlayerMovementRejectionReason.StaleInput,
                9),
            (
                AuthoritativePlayerMovementRejectionReason
                    .TransportEpoch,
                10),
            (
                AuthoritativePlayerMovementRejectionReason
                    .TransportSource,
                11),
            (
                AuthoritativePlayerMovementRejectionReason.Overloaded,
                12)
        };
        foreach (var (reason, value) in reasons)
        {
            Check.Equal(
                value,
                (int)reason,
                $"locked movement rejection value {reason}");
        }

        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new AuthoritativePlayerMovementPolicy(
                baseMaximumSpeed: 0f),
            "zero base movement speed is invalid policy");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new AuthoritativePlayerMovementPolicy(
                elapsedCreditCap: TimeSpan.Zero),
            "zero elapsed credit cap is invalid policy");
    }

    private static void CheckBaselineAndDecisionProjection()
    {
        var system = CreateSystem(
            x: 40f,
            z: -20f,
            state: 7,
            auxiliary: 0.5f);
        var before = system.Snapshot;
        Check.Equal(
            40f,
            before.AuthoritativeX,
            "baseline starts at server authoritative X");
        Check.Equal(
            -20f,
            before.AuthoritativeZ,
            "baseline starts at server authoritative Z");

        var decision = system.ProcessLatest(
            Input(
                id: 1,
                x: 40.5f,
                z: -20f,
                state: 99,
                auxiliary: 1.25f),
            World(),
            TimeSpan.FromMilliseconds(50));

        Check.True(
            decision.Accepted &&
            decision.RejectionReason ==
                AuthoritativePlayerMovementRejectionReason.None,
            "ordinary bounded movement is accepted");
        Check.Equal(
            1UL,
            decision.SimulationTick,
            "movement is processed on one fixed tick");
        Check.Equal(
            1UL,
            decision.Revision,
            "accepted movement advances revision");
        Check.Equal(
            1UL,
            decision.AcknowledgedInputId,
            "accepted movement advances input acknowledgement");
        Check.Equal(
            40.5f,
            decision.AuthoritativeX,
            "accepted X becomes authoritative");
        Check.Equal(
            99U,
            decision.OpaqueState,
            "accepted opaque state becomes authoritative");
        Check.Equal(
            1.25f,
            decision.AuthoritativeAuxiliary,
            "accepted auxiliary value becomes authoritative");
    }

    private static void CheckSpeedBoundaries()
    {
        const float exactAllowance = 1.15f;
        var exact = CreateSystem();
        var accepted = exact.ProcessLatest(
            Input(id: 1, x: exactAllowance),
            World(),
            TimeSpan.FromMilliseconds(50));
        Check.True(
            accepted.Accepted,
            "exact elapsed speed boundary is accepted");

        var epsilon = CreateSystem();
        var rejected = epsilon.ProcessLatest(
            Input(
                id: 1,
                x: MathF.BitIncrement(exactAllowance)),
            World(),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            epsilon,
            rejected,
            AuthoritativePlayerMovementRejectionReason.Speed,
            "epsilon above elapsed speed allowance");
        Check.Equal(
            1UL,
            rejected.AcknowledgedInputId,
            "speed correction acknowledges processed input");
        Check.Equal(
            0UL,
            rejected.Revision,
            "speed correction cannot advance position revision");

        var noClientClock = CreateSystem();
        var forgedState = noClientClock.ProcessLatest(
            Input(
                id: 1,
                x: 2f,
                state: uint.MaxValue,
                auxiliary: float.MaxValue),
            World(),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            noClientClock,
            forgedState,
            AuthoritativePlayerMovementRejectionReason.Speed,
            "client state and auxiliary values cannot create time credit");
    }

    private static void CheckDistanceCreditCap()
    {
        const float exactCappedAllowance = 8.75f;
        var exact = CreateSystem();
        var accepted = exact.ProcessLatest(
            Input(id: 1, x: exactCappedAllowance),
            World(),
            TimeSpan.FromMinutes(10));
        Check.True(
            accepted.Accepted,
            "exact one-second absolute distance boundary is accepted");

        var epsilon = CreateSystem();
        var rejected = epsilon.ProcessLatest(
            Input(
                id: 1,
                x: MathF.BitIncrement(exactCappedAllowance)),
            World(),
            TimeSpan.FromMinutes(10));
        AssertAtomicRejection(
            epsilon,
            rejected,
            AuthoritativePlayerMovementRejectionReason.Distance,
            "long idle cannot bank teleport distance");
        Check.Equal(
            1UL,
            rejected.AcknowledgedInputId,
            "distance correction acknowledges processed input");
        Check.Equal(
            0UL,
            rejected.Revision,
            "distance correction cannot advance position revision");
    }

    private static void CheckMountedMultiplier()
    {
        const float mountedAllowance = 1.55f;
        var mounted = CreateSystem();
        var mountedDecision = mounted.ProcessLatest(
            Input(id: 1, x: mountedAllowance),
            World(multiplier: 2f),
            TimeSpan.FromMilliseconds(50));
        Check.True(
            mountedDecision.Accepted,
            "server-owned mounted multiplier expands allowance");

        var unmounted = CreateSystem();
        var unmountedDecision = unmounted.ProcessLatest(
            Input(id: 1, x: mountedAllowance),
            World(multiplier: 1f),
            TimeSpan.FromMilliseconds(50));
        AssertAtomicRejection(
            unmounted,
            unmountedDecision,
            AuthoritativePlayerMovementRejectionReason.Speed,
            "same step is rejected without mounted multiplier");
    }

    private static void CheckCadenceAndEmptyTicks()
    {
        var system = CreateSystem();
        var first = system.ProcessLatest(
            Input(id: 1),
            World(),
            TimeSpan.FromMilliseconds(50));
        Check.True(first.Accepted, "cadence fixture baseline input");

        var cadence = system.ProcessLatest(
            Input(id: 2, x: 0.1f),
            World(),
            TimeSpan.FromMilliseconds(69));
        AssertAtomicRejection(
            system,
            cadence,
            AuthoritativePlayerMovementRejectionReason.Cadence,
            "input below the 20 ms cadence is rejected");
        Check.Equal(
            2UL,
            cadence.AcknowledgedInputId,
            "cadence correction acknowledges processed input");
        Check.Equal(
            first.Revision,
            cadence.Revision,
            "cadence correction retains position revision");

        var boundary = system.ProcessLatest(
            Input(id: 3, x: 0.1f),
            World(),
            TimeSpan.FromMilliseconds(89));
        Check.True(
            boundary.Accepted,
            "input at the exact 20 ms cadence is accepted");

        var empty = system.AdvanceWithoutInput();
        Check.Equal(
            4UL,
            empty.SimulationTick,
            "empty fixed step advances simulation tick");
        Check.Equal(
            boundary.Revision,
            empty.Revision,
            "empty fixed step does not advance movement revision");
    }
}
