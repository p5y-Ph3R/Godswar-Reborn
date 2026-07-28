using System.Collections.ObjectModel;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game.Simulation.Replay;

/// <summary>
/// One semantic movement decision at a fixed simulation tick. An empty frame
/// represents packet loss, an idle client, or a tick with no eligible input.
/// </summary>
internal readonly record struct MovementReplayFrame
{
    public MovementReplayFrame(
        in AuthoritativePlayerMovementInput input)
    {
        Input = input;
    }

    public AuthoritativePlayerMovementInput? Input { get; }

    public bool HasInput => Input.HasValue;

    public static MovementReplayFrame Empty => default;
}

/// <summary>
/// Bounded, versioned semantic input for deterministic movement replay.
/// It begins after authentication and transport validation, so it never
/// contains credentials, tickets, cookies, keys, addresses, or raw packets.
/// </summary>
internal sealed class MovementReplayTrace
{
    public const ushort CurrentVersion = 1;
    public const int MaximumFrameCount = 24_000;
    public const int MaximumInputCount = 12_000;

    private readonly ReadOnlyCollection<MovementReplayFrame> _frames;

    public MovementReplayTrace(
        in AuthoritativePlayerMovementBaseline baseline,
        in AuthoritativePlayerMovementWorldContext world,
        IReadOnlyList<MovementReplayFrame> frames,
        ushort version = CurrentVersion)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (version != CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                "The movement replay trace version is not supported.");
        }
        if (frames.Count > MaximumFrameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frames),
                $"A movement replay is limited to {MaximumFrameCount} frames.");
        }

        var copy = new MovementReplayFrame[frames.Count];
        var inputCount = 0;
        for (var index = 0; index < frames.Count; index++)
        {
            var frame = frames[index];
            copy[index] = frame;
            if (frame.HasInput)
            {
                inputCount = checked(inputCount + 1);
                if (inputCount > MaximumInputCount)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(frames),
                        $"A movement replay is limited to {MaximumInputCount} inputs.");
                }
            }
        }

        _ = GetTimelineTicks(
            baseline.ServerTimestamp,
            frames.Count);

        Version = version;
        Baseline = baseline;
        World = world;
        InputCount = inputCount;
        _frames = Array.AsReadOnly(copy);
    }

    public ushort Version { get; }

    public AuthoritativePlayerMovementBaseline Baseline { get; }

    public AuthoritativePlayerMovementWorldContext World { get; }

    public IReadOnlyList<MovementReplayFrame> Frames => _frames;

    public int InputCount { get; }

    internal static TimeSpan GetFrameTimestamp(
        TimeSpan timelineOrigin,
        int frameIndex)
    {
        if (frameIndex < 0 || frameIndex >= MaximumFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        }

        return TimeSpan.FromTicks(
            GetTimelineTicks(
                timelineOrigin,
                checked(frameIndex + 1)));
    }

    private static long GetTimelineTicks(
        TimeSpan timelineOrigin,
        int completedFrames)
    {
        try
        {
            var offset = checked(
                (long)completedFrames *
                AuthoritativePlayerMovementPolicy.FixedStep.Ticks);
            return checked(timelineOrigin.Ticks + offset);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timelineOrigin),
                "The replay timeline exceeds TimeSpan range.");
        }
    }
}
