using Godswar.Server.Game.Simulation.Replay;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static class Phase5DeterministicMovementReplayChecks
{
    private const uint TransportEpoch = 9;
    private const uint WorldGeneration = 27;
    private const byte MapId = 4;
    private const uint SourceObjectId = 0x5017;

    public static Task RunAsync()
    {
        CheckSameTraceProducesCanonicalOutcome();
        CheckChangedInputFindsFirstDivergence();
        CheckCheckpointRehydrateMatchesUninterruptedSuffix();
        CheckTraceBudgetsRejectExcessInput();
        CheckLossDuplicateAndReorderingInvariants();
        CheckUnsafeCheckpointIsRefused();
        return Task.CompletedTask;
    }

    private static void CheckSameTraceProducesCanonicalOutcome()
    {
        var trace = CreateNominalTrace();
        var first = new MovementReplayRunner().Run(trace);
        var second = new MovementReplayRunner().Run(trace);

        Check.Equal(
            first.TraceHeaderHash,
            second.TraceHeaderHash,
            "same replay has the same canonical header");
        Check.Equal(
            first.OutcomeHash,
            second.OutcomeHash,
            "same replay has the same canonical outcome");
        Check.Equal(
            64,
            first.OutcomeHash.Length,
            "SHA-256 replay outcome is lowercase hexadecimal");
        Check.Equal(
            first.FinalSnapshot,
            second.FinalSnapshot,
            "same replay has the same final snapshot");
        Check.True(
            MovementReplayComparer.FindFirstDivergence(
                first,
                second) is null,
            "identical runs have no divergence");

        Check.Equal(
            "f1f7eb65948d7faf925cb41ade6b16572e230f7e2c897b850903ffb0ab7285cc",
            first.OutcomeHash,
            "version-one semantic replay golden outcome");
    }

    private static void CheckChangedInputFindsFirstDivergence()
    {
        var baseline = Baseline();
        var world = World();
        var expectedTrace = new MovementReplayTrace(
            baseline,
            world,
            [Frame(id: 1, x: 0.25f)]);
        var changedTrace = new MovementReplayTrace(
            baseline,
            world,
            [Frame(id: 1, x: 0.50f)]);
        var runner = new MovementReplayRunner();
        var expected = runner.Run(expectedTrace);
        var actual = runner.Run(changedTrace);

        Check.True(
            !string.Equals(
                expected.OutcomeHash,
                actual.OutcomeHash,
                StringComparison.Ordinal),
            "an accepted input change changes the outcome hash");

        var divergence =
            MovementReplayComparer.FindFirstDivergence(
                expected,
                actual);
        Check.True(
            divergence.HasValue,
            "an accepted input change has diagnostics");
        Check.Equal(
            0,
            divergence!.Value.FrameIndex,
            "diagnostics identify the first divergent frame");
        Check.Equal(
            "Decision",
            divergence.Value.Field,
            "diagnostics identify the first divergent projection");
    }

    private static void
        CheckCheckpointRehydrateMatchesUninterruptedSuffix()
    {
        var trace = CreateNominalTrace();
        var runner = new MovementReplayRunner();
        var uninterrupted = runner.Run(trace);
        var checkpoint = runner.CreateCheckpoint(
            trace,
            completedFrameCount: 2);
        var resumed = runner.Resume(trace, checkpoint);

        Check.Equal(
            2,
            checkpoint.NextFrameIndex,
            "checkpoint records the suffix boundary");
        Check.Equal(
            2,
            resumed.FirstFrameIndex,
            "resumed outcomes begin at the suffix boundary");
        Check.Equal(
            trace.Frames.Count - 2,
            resumed.Outcomes.Count,
            "resumed run only retains suffix outcomes");
        Check.Equal(
            uninterrupted.OutcomeHash,
            resumed.OutcomeHash,
            "checkpoint rehydrate preserves the full hash chain");
        Check.Equal(
            uninterrupted.FinalSnapshot,
            resumed.FinalSnapshot,
            "checkpoint rehydrate preserves final authority");
    }

    private static void CheckTraceBudgetsRejectExcessInput()
    {
        var excessiveInputs =
            new MovementReplayFrame[
                MovementReplayTrace.MaximumInputCount + 1];
        var input = Input(id: 1, x: 0.25f);
        Array.Fill(excessiveInputs, new MovementReplayFrame(input));

        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new MovementReplayTrace(
                Baseline(),
                World(),
                excessiveInputs),
            "movement trace rejects input beyond its hard budget");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new MovementReplayTrace(
                Baseline(),
                World(),
                [],
                version:
                    checked(
                        (ushort)(
                            MovementReplayTrace.CurrentVersion + 1))),
            "movement trace rejects an unknown semantic version");
    }

    private static void CheckLossDuplicateAndReorderingInvariants()
    {
        var impaired = new MovementReplayTrace(
            Baseline(),
            World(),
            [
                Frame(id: 1, x: 0.25f),
                MovementReplayFrame.Empty,
                Frame(id: 3, x: 0.50f),
                Frame(id: 3, x: 0.75f),
                Frame(id: 2, x: 0.75f),
                Frame(id: 4, x: 0.75f)
            ]);
        var clean = new MovementReplayTrace(
            Baseline(),
            World(),
            [
                Frame(id: 1, x: 0.25f),
                MovementReplayFrame.Empty,
                Frame(id: 3, x: 0.50f),
                MovementReplayFrame.Empty,
                MovementReplayFrame.Empty,
                Frame(id: 4, x: 0.75f)
            ]);
        var runner = new MovementReplayRunner();
        var first = runner.Run(impaired);
        var repeated = runner.Run(impaired);
        var cleanResult = runner.Run(clean);

        Check.Equal(
            first.OutcomeHash,
            repeated.OutcomeHash,
            "loss, duplicate, and reorder schedule remains deterministic");
        Check.Equal(
            cleanResult.FinalSnapshot,
            first.FinalSnapshot,
            "stale duplicate and reordered inputs cannot change authority");
        Check.True(
            first.Outcomes[1].Decision is null,
            "lost input is represented by an empty fixed tick");
        Check.True(
            IsStaleRejection(first.Outcomes[3]),
            "duplicate input is rejected as stale");
        Check.True(
            IsStaleRejection(first.Outcomes[4]),
            "reordered older input is rejected as stale");
        Check.Equal(
            3UL,
            first.FinalSnapshot.Revision,
            "only unique current inputs advance the revision");
        Check.Equal(
            4UL,
            first.FinalSnapshot.AcknowledgedInputId,
            "latest valid input remains acknowledged");
    }

    private static void CheckUnsafeCheckpointIsRefused()
    {
        var trace = new MovementReplayTrace(
            Baseline(),
            World(),
            [
                Frame(id: 1, x: 0.25f),
                Frame(id: 1, x: 0.50f)
            ]);

        Check.Throws<InvalidOperationException>(
            () => _ = new MovementReplayRunner()
                .CreateCheckpoint(trace, completedFrameCount: 2),
            "checkpoint does not hide non-representable rejection state");
    }

    private static MovementReplayTrace CreateNominalTrace()
    {
        var baseline = Baseline();
        var world = World();
        return new MovementReplayTrace(
            baseline,
            world,
            [
                Frame(id: 1, x: 0.25f),
                MovementReplayFrame.Empty,
                Frame(
                    id: 2,
                    x: 0.50f,
                    source:
                        AuthoritativePlayerMovementSource.Udp),
                MovementReplayFrame.Empty,
                Frame(id: 3, x: 0.75f)
            ]);
    }

    private static bool IsStaleRejection(
        in MovementReplayFrameOutcome outcome) =>
        outcome.Decision is
        {
            Accepted: false,
            RejectionReason:
                AuthoritativePlayerMovementRejectionReason.StaleInput
        };

    private static MovementReplayFrame Frame(
        ulong id,
        float x,
        AuthoritativePlayerMovementSource source =
            AuthoritativePlayerMovementSource.Tls)
    {
        var input = Input(id, x, source);
        return new MovementReplayFrame(input);
    }

    private static AuthoritativePlayerMovementInput Input(
        ulong id,
        float x,
        AuthoritativePlayerMovementSource source =
            AuthoritativePlayerMovementSource.Tls) =>
        new(
            TransportEpoch,
            id,
            WorldGeneration,
            MapId,
            OpaqueState: checked((uint)id),
            TargetX: x,
            TargetZ: 0f,
            Auxiliary: 0.5f,
            SourceObjectId,
            source,
            TargetsCurrentWorld: true);

    private static AuthoritativePlayerMovementBaseline Baseline() =>
        new(
            TransportEpoch,
            WorldGeneration,
            MapId,
            SourceObjectId,
            OpaqueState: 0,
            CurrentX: 0f,
            CurrentZ: 0f,
            Auxiliary: 0f,
            ServerTimestamp: TimeSpan.FromHours(1));

    private static AuthoritativePlayerMovementWorldContext World() =>
        new(
            TransportEpoch,
            WorldGeneration,
            MapId,
            SourceObjectId,
            IsReady: true,
            IsAlive: true,
            MovementMultiplier: 1f,
            AllowedSources:
                AuthoritativePlayerMovementSource.Tls |
                AuthoritativePlayerMovementSource.Udp);
}
