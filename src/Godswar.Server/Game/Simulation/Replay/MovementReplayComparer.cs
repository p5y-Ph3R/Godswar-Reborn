using System.Globalization;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game.Simulation.Replay;

internal readonly record struct MovementReplayDivergence(
    int FrameIndex,
    string Field,
    string Expected,
    string Actual);

internal static class MovementReplayComparer
{
    public static MovementReplayDivergence? FindFirstDivergence(
        MovementReplayResult expected,
        MovementReplayResult actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        if (!string.Equals(
                expected.TraceHeaderHash,
                actual.TraceHeaderHash,
                StringComparison.Ordinal))
        {
            return new MovementReplayDivergence(
                -1,
                "TraceHeader",
                expected.TraceHeaderHash,
                actual.TraceHeaderHash);
        }
        if (expected.FirstFrameIndex != actual.FirstFrameIndex)
        {
            return new MovementReplayDivergence(
                Math.Min(
                    expected.FirstFrameIndex,
                    actual.FirstFrameIndex),
                "FirstFrameIndex",
                expected.FirstFrameIndex.ToString(
                    CultureInfo.InvariantCulture),
                actual.FirstFrameIndex.ToString(
                    CultureInfo.InvariantCulture));
        }

        var commonCount = Math.Min(
            expected.Outcomes.Count,
            actual.Outcomes.Count);
        for (var index = 0; index < commonCount; index++)
        {
            var expectedFrame = expected.Outcomes[index];
            var actualFrame = actual.Outcomes[index];
            var divergence =
                CompareFrame(expectedFrame, actualFrame);
            if (divergence.HasValue)
            {
                return divergence;
            }
        }

        if (expected.Outcomes.Count != actual.Outcomes.Count)
        {
            var frameIndex =
                expected.FirstFrameIndex + commonCount;
            return new MovementReplayDivergence(
                frameIndex,
                "OutcomeCount",
                expected.Outcomes.Count.ToString(
                    CultureInfo.InvariantCulture),
                actual.Outcomes.Count.ToString(
                    CultureInfo.InvariantCulture));
        }
        if (!string.Equals(
                expected.OutcomeHash,
                actual.OutcomeHash,
                StringComparison.Ordinal))
        {
            return new MovementReplayDivergence(
                expected.CompletedFrameCount,
                "OutcomeHashChain",
                expected.OutcomeHash,
                actual.OutcomeHash);
        }

        return null;
    }

    private static MovementReplayDivergence? CompareFrame(
        in MovementReplayFrameOutcome expected,
        in MovementReplayFrameOutcome actual)
    {
        if (expected.FrameIndex != actual.FrameIndex)
        {
            return Difference(
                expected.FrameIndex,
                "FrameIndex",
                expected.FrameIndex,
                actual.FrameIndex);
        }
        if (expected.ServerReceivedAt != actual.ServerReceivedAt)
        {
            return Difference(
                expected.FrameIndex,
                "ServerReceivedAtTicks",
                expected.ServerReceivedAt.Ticks,
                actual.ServerReceivedAt.Ticks);
        }
        if (expected.HadInput != actual.HadInput)
        {
            return Difference(
                expected.FrameIndex,
                "HadInput",
                expected.HadInput,
                actual.HadInput);
        }
        if (!Nullable.Equals(expected.Decision, actual.Decision))
        {
            return new MovementReplayDivergence(
                expected.FrameIndex,
                "Decision",
                FormatDecision(expected.Decision),
                FormatDecision(actual.Decision));
        }
        if (expected.Snapshot != actual.Snapshot)
        {
            return new MovementReplayDivergence(
                expected.FrameIndex,
                "Snapshot",
                FormatSnapshot(expected.Snapshot),
                FormatSnapshot(actual.Snapshot));
        }

        return null;
    }

    private static MovementReplayDivergence Difference<T>(
        int frameIndex,
        string field,
        T expected,
        T actual) =>
        new(
            frameIndex,
            field,
            Convert.ToString(expected, CultureInfo.InvariantCulture) ??
                string.Empty,
            Convert.ToString(actual, CultureInfo.InvariantCulture) ??
                string.Empty);

    private static string FormatDecision(
        AuthoritativePlayerMovementDecision? decision)
    {
        if (decision is not { } value)
        {
            return "none";
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"accepted={value.Accepted};reason={(byte)value.RejectionReason};" +
            $"tick={value.SimulationTick};revision={value.Revision};" +
            $"input={value.InputId};ack={value.AcknowledgedInputId};" +
            $"x={FloatBits(value.AuthoritativeX)};" +
            $"z={FloatBits(value.AuthoritativeZ)};" +
            $"aux={FloatBits(value.AuthoritativeAuxiliary)}");
    }

    private static string FormatSnapshot(
        in AuthoritativePlayerMovementSnapshot snapshot) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"tick={snapshot.SimulationTick};revision={snapshot.Revision};" +
            $"ack={snapshot.AcknowledgedInputId};" +
            $"x={FloatBits(snapshot.AuthoritativeX)};" +
            $"z={FloatBits(snapshot.AuthoritativeZ)};" +
            $"aux={FloatBits(snapshot.AuthoritativeAuxiliary)}");

    private static string FloatBits(float value) =>
        $"0x{BitConverter.SingleToInt32Bits(value):X8}";
}
