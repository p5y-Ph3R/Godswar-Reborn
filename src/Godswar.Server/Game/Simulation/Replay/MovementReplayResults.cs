using System.Collections.ObjectModel;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game.Simulation.Replay;

internal readonly record struct MovementReplayFrameOutcome(
    int FrameIndex,
    TimeSpan ServerReceivedAt,
    bool HadInput,
    AuthoritativePlayerMovementDecision? Decision,
    AuthoritativePlayerMovementSnapshot Snapshot);

internal sealed class MovementReplayResult
{
    private readonly ReadOnlyCollection<MovementReplayFrameOutcome> _outcomes;

    internal MovementReplayResult(
        ushort version,
        int firstFrameIndex,
        int completedFrameCount,
        AuthoritativePlayerMovementSnapshot finalSnapshot,
        IReadOnlyList<MovementReplayFrameOutcome> outcomes,
        ReadOnlySpan<byte> traceHeaderHash,
        ReadOnlySpan<byte> outcomeHash)
    {
        if (traceHeaderHash.Length != MovementReplayCanonicalHash.HashBytes ||
            outcomeHash.Length != MovementReplayCanonicalHash.HashBytes)
        {
            throw new ArgumentException(
                "Replay hashes must be SHA-256 values.");
        }

        Version = version;
        FirstFrameIndex = firstFrameIndex;
        CompletedFrameCount = completedFrameCount;
        FinalSnapshot = finalSnapshot;
        TraceHeaderHash =
            MovementReplayCanonicalHash.ToHex(traceHeaderHash);
        OutcomeHash =
            MovementReplayCanonicalHash.ToHex(outcomeHash);
        _outcomes = Array.AsReadOnly(outcomes.ToArray());
    }

    public ushort Version { get; }

    public int FirstFrameIndex { get; }

    public int CompletedFrameCount { get; }

    public AuthoritativePlayerMovementSnapshot FinalSnapshot { get; }

    public IReadOnlyList<MovementReplayFrameOutcome> Outcomes => _outcomes;

    public string TraceHeaderHash { get; }

    public string OutcomeHash { get; }
}

internal sealed class MovementReplayCheckpoint
{
    private readonly byte[] _traceHeaderHash;
    private readonly byte[] _traceIdentityHash;
    private readonly byte[] _outcomeHash;

    internal MovementReplayCheckpoint(
        ushort version,
        int nextFrameIndex,
        TimeSpan timelineOrigin,
        in AuthoritativePlayerMovementBaseline baseline,
        ReadOnlySpan<byte> traceHeaderHash,
        ReadOnlySpan<byte> traceIdentityHash,
        ReadOnlySpan<byte> outcomeHash)
    {
        if (traceHeaderHash.Length != MovementReplayCanonicalHash.HashBytes ||
            traceIdentityHash.Length != MovementReplayCanonicalHash.HashBytes ||
            outcomeHash.Length != MovementReplayCanonicalHash.HashBytes)
        {
            throw new ArgumentException(
                "Replay hashes must be SHA-256 values.");
        }

        Version = version;
        NextFrameIndex = nextFrameIndex;
        TimelineOrigin = timelineOrigin;
        Baseline = baseline;
        _traceHeaderHash = traceHeaderHash.ToArray();
        _traceIdentityHash = traceIdentityHash.ToArray();
        _outcomeHash = outcomeHash.ToArray();
    }

    public ushort Version { get; }

    public int NextFrameIndex { get; }

    public TimeSpan TimelineOrigin { get; }

    public AuthoritativePlayerMovementBaseline Baseline { get; }

    public string TraceHeaderHash =>
        MovementReplayCanonicalHash.ToHex(_traceHeaderHash);

    public string OutcomeHash =>
        MovementReplayCanonicalHash.ToHex(_outcomeHash);

    internal ReadOnlySpan<byte> TraceIdentityHashBytes =>
        _traceIdentityHash;

    internal ReadOnlySpan<byte> TraceHeaderHashBytes =>
        _traceHeaderHash;

    internal ReadOnlySpan<byte> OutcomeHashBytes =>
        _outcomeHash;
}
