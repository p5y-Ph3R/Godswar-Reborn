using System.Security.Cryptography;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game.Simulation.Replay;

/// <summary>
/// Replays post-authentication movement semantics at exactly 20 Hz. It does
/// not perform network I/O, wait on wall-clock time, or access persistence.
/// </summary>
internal sealed class MovementReplayRunner
{
    private static readonly TimeSpan RequiredFixedStep =
        TimeSpan.FromMilliseconds(50);

    public MovementReplayResult Run(MovementReplayTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        EnsureFixedStep();

        var headerHash =
            MovementReplayCanonicalHash.CreateHeader(trace);
        var execution = Execute(
            trace,
            trace.Baseline,
            startFrameIndex: 0,
            endFrameIndex: trace.Frames.Count,
            timelineOrigin: trace.Baseline.ServerTimestamp,
            initialOutcomeHash: headerHash,
            captureOutcomes: true);

        return CreateResult(
            trace,
            firstFrameIndex: 0,
            execution,
            headerHash);
    }

    public MovementReplayCheckpoint CreateCheckpoint(
        MovementReplayTrace trace,
        int completedFrameCount)
    {
        ArgumentNullException.ThrowIfNull(trace);
        EnsureFixedStep();
        if (completedFrameCount < 0 ||
            completedFrameCount > trace.Frames.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedFrameCount));
        }

        var headerHash =
            MovementReplayCanonicalHash.CreateHeader(trace);
        var execution = Execute(
            trace,
            trace.Baseline,
            startFrameIndex: 0,
            endFrameIndex: completedFrameCount,
            timelineOrigin: trace.Baseline.ServerTimestamp,
            initialOutcomeHash: headerHash,
            captureOutcomes: false);
        if (!execution.IsCheckpointRepresentable)
        {
            throw new InvalidOperationException(
                "A rejected movement input advanced hidden cadence state. " +
                "Checkpoint after a later accepted input instead.");
        }

        var baseline = CreateBaseline(
            execution.FinalSnapshot,
            execution.LastAcceptedTimestamp,
            trace.Baseline.SourceObjectId);
        var traceIdentityHash =
            MovementReplayCanonicalHash.CreateTraceIdentity(
                trace,
                headerHash);
        return new MovementReplayCheckpoint(
            trace.Version,
            completedFrameCount,
            trace.Baseline.ServerTimestamp,
            baseline,
            headerHash,
            traceIdentityHash,
            execution.OutcomeHash);
    }

    public MovementReplayResult Resume(
        MovementReplayTrace trace,
        MovementReplayCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(checkpoint);
        EnsureFixedStep();
        if (checkpoint.Version != trace.Version)
        {
            throw new ArgumentException(
                "The checkpoint version does not match the trace.",
                nameof(checkpoint));
        }
        if (checkpoint.NextFrameIndex < 0 ||
            checkpoint.NextFrameIndex > trace.Frames.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkpoint),
                "The checkpoint is outside the trace.");
        }
        if (checkpoint.TimelineOrigin !=
            trace.Baseline.ServerTimestamp)
        {
            throw new ArgumentException(
                "The checkpoint timeline does not match the trace.",
                nameof(checkpoint));
        }

        var headerHash =
            MovementReplayCanonicalHash.CreateHeader(trace);
        if (!CryptographicOperations.FixedTimeEquals(
                headerHash,
                checkpoint.TraceHeaderHashBytes))
        {
            throw new ArgumentException(
                "The checkpoint belongs to a different replay trace.",
                nameof(checkpoint));
        }
        var traceIdentityHash =
            MovementReplayCanonicalHash.CreateTraceIdentity(
                trace,
                headerHash);
        if (!CryptographicOperations.FixedTimeEquals(
                traceIdentityHash,
                checkpoint.TraceIdentityHashBytes))
        {
            throw new ArgumentException(
                "The checkpoint belongs to a different replay input trace.",
                nameof(checkpoint));
        }

        var execution = Execute(
            trace,
            checkpoint.Baseline,
            checkpoint.NextFrameIndex,
            trace.Frames.Count,
            checkpoint.TimelineOrigin,
            checkpoint.OutcomeHashBytes,
            captureOutcomes: true);
        return CreateResult(
            trace,
            checkpoint.NextFrameIndex,
            execution,
            headerHash);
    }

    private static ReplayExecution Execute(
        MovementReplayTrace trace,
        in AuthoritativePlayerMovementBaseline baseline,
        int startFrameIndex,
        int endFrameIndex,
        TimeSpan timelineOrigin,
        ReadOnlySpan<byte> initialOutcomeHash,
        bool captureOutcomes)
    {
        var movement =
            new AuthoritativePlayerMovementSystem(baseline);
        var outcomeHash = initialOutcomeHash.ToArray();
        var outcomes = captureOutcomes
            ? new List<MovementReplayFrameOutcome>(
                endFrameIndex - startFrameIndex)
            : null;
        var lastAcceptedTimestamp = baseline.ServerTimestamp;
        var checkpointRepresentable = true;

        for (var index = startFrameIndex;
             index < endFrameIndex;
             index++)
        {
            var timestamp =
                MovementReplayTrace.GetFrameTimestamp(
                    timelineOrigin,
                    index);
            var frame = trace.Frames[index];
            AuthoritativePlayerMovementDecision? decision = null;
            AuthoritativePlayerMovementSnapshot snapshot;

            if (frame.Input is { } input)
            {
                var processed = movement.ProcessLatest(
                    input,
                    trace.World,
                    timestamp);
                decision = processed;
                snapshot = movement.Snapshot;
                if (processed.Accepted)
                {
                    lastAcceptedTimestamp = timestamp;
                    checkpointRepresentable = true;
                }
                else
                {
                    // The current movement baseline intentionally omits the
                    // distinct observed/accepted timestamps after rejection.
                    checkpointRepresentable = false;
                }
            }
            else
            {
                snapshot = movement.AdvanceWithoutInput();
            }

            var outcome = new MovementReplayFrameOutcome(
                index,
                timestamp,
                frame.HasInput,
                decision,
                snapshot);
            outcomeHash =
                MovementReplayCanonicalHash.Append(
                    outcomeHash,
                    outcome);
            outcomes?.Add(outcome);
        }

        return new ReplayExecution(
            movement.Snapshot,
            outcomeHash,
            outcomes ?? [],
            lastAcceptedTimestamp,
            checkpointRepresentable);
    }

    private static MovementReplayResult CreateResult(
        MovementReplayTrace trace,
        int firstFrameIndex,
        in ReplayExecution execution,
        ReadOnlySpan<byte> headerHash) =>
        new(
            trace.Version,
            firstFrameIndex,
            trace.Frames.Count,
            execution.FinalSnapshot,
            execution.Outcomes,
            headerHash,
            execution.OutcomeHash);

    private static AuthoritativePlayerMovementBaseline CreateBaseline(
        in AuthoritativePlayerMovementSnapshot snapshot,
        TimeSpan lastAcceptedTimestamp,
        uint sourceObjectId) =>
        new(
            snapshot.TransportEpoch,
            snapshot.WorldGeneration,
            snapshot.MapId,
            sourceObjectId,
            snapshot.OpaqueState,
            snapshot.AuthoritativeX,
            snapshot.AuthoritativeZ,
            snapshot.AuthoritativeAuxiliary,
            lastAcceptedTimestamp,
            snapshot.AcknowledgedInputId,
            snapshot.Revision,
            snapshot.SimulationTick);

    private static void EnsureFixedStep()
    {
        if (AuthoritativePlayerMovementPolicy.FixedStep !=
            RequiredFixedStep)
        {
            throw new InvalidOperationException(
                "Movement replay version 1 requires an exact 50 ms step.");
        }
    }

    private readonly record struct ReplayExecution(
        AuthoritativePlayerMovementSnapshot FinalSnapshot,
        byte[] OutcomeHash,
        IReadOnlyList<MovementReplayFrameOutcome> Outcomes,
        TimeSpan LastAcceptedTimestamp,
        bool IsCheckpointRepresentable);
}
