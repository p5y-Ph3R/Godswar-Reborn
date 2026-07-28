using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game.Simulation.Replay;

/// <summary>
/// Canonical little-endian SHA-256 hash chain for semantic replay outcomes.
/// The chain can be carried through a safe checkpoint without retaining the
/// bounded prefix in memory.
/// </summary>
internal static class MovementReplayCanonicalHash
{
    private static readonly byte[] HeaderDomain =
        Encoding.ASCII.GetBytes("GODSWAR-MOVEMENT-REPLAY-HEADER-V1");

    private static readonly byte[] FrameDomain =
        Encoding.ASCII.GetBytes("GODSWAR-MOVEMENT-REPLAY-FRAME-V1");

    private static readonly byte[] TraceIdentityDomain =
        Encoding.ASCII.GetBytes("GODSWAR-MOVEMENT-REPLAY-TRACE-V1");

    public const int HashBytes = 32;

    public static byte[] CreateHeader(
        MovementReplayTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);

        var writer = new CanonicalWriter(192);
        writer.WriteBytes(HeaderDomain);
        writer.WriteUInt16(trace.Version);
        writer.WriteInt64(
            AuthoritativePlayerMovementPolicy.FixedStep.Ticks);

        var policy = new AuthoritativePlayerMovementPolicy();
        writer.WriteSingle(policy.BaseMaximumSpeed);
        writer.WriteSingle(policy.PositionTolerance);
        writer.WriteInt64(policy.ElapsedCreditCap.Ticks);
        writer.WriteInt64(policy.MinimumInputCadence.Ticks);
        writer.WriteSingle(policy.MaximumMovementMultiplier);

        WriteBaseline(writer, trace.Baseline);
        WriteWorld(writer, trace.World);
        return SHA256.HashData(writer.ToArray());
    }

    public static byte[] Append(
        ReadOnlySpan<byte> previousHash,
        in MovementReplayFrameOutcome outcome)
    {
        if (previousHash.Length != HashBytes)
        {
            throw new ArgumentException(
                "The previous replay hash must be a SHA-256 value.",
                nameof(previousHash));
        }

        var writer = new CanonicalWriter(256);
        writer.WriteBytes(FrameDomain);
        writer.WriteBytes(previousHash);
        writer.WriteInt32(outcome.FrameIndex);
        writer.WriteInt64(outcome.ServerReceivedAt.Ticks);
        writer.WriteBoolean(outcome.HadInput);

        if (outcome.HadInput)
        {
            if (outcome.Decision is not { } decision)
            {
                throw new ArgumentException(
                    "An input frame must carry a movement decision.",
                    nameof(outcome));
            }

            WriteDecision(writer, decision);
        }
        else if (outcome.Decision.HasValue)
        {
            throw new ArgumentException(
                "An empty frame cannot carry a movement decision.",
                nameof(outcome));
        }

        WriteSnapshot(writer, outcome.Snapshot);
        return SHA256.HashData(writer.ToArray());
    }

    public static byte[] CreateTraceIdentity(
        MovementReplayTrace trace,
        ReadOnlySpan<byte> headerHash)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (headerHash.Length != HashBytes)
        {
            throw new ArgumentException(
                "The replay header hash must be a SHA-256 value.",
                nameof(headerHash));
        }

        var writer = new CanonicalWriter(
            checked(64 + trace.Frames.Count * 48));
        writer.WriteBytes(TraceIdentityDomain);
        writer.WriteBytes(headerHash);
        writer.WriteInt32(trace.Frames.Count);
        foreach (var frame in trace.Frames)
        {
            writer.WriteBoolean(frame.HasInput);
            if (frame.Input is { } input)
            {
                WriteInput(writer, input);
            }
        }

        return SHA256.HashData(writer.ToArray());
    }

    public static string ToHex(ReadOnlySpan<byte> hash) =>
        Convert.ToHexString(hash).ToLowerInvariant();

    private static void WriteBaseline(
        CanonicalWriter writer,
        in AuthoritativePlayerMovementBaseline baseline)
    {
        writer.WriteUInt32(baseline.TransportEpoch);
        writer.WriteUInt32(baseline.WorldGeneration);
        writer.WriteByte(baseline.MapId);
        writer.WriteUInt32(baseline.SourceObjectId);
        writer.WriteUInt32(baseline.OpaqueState);
        writer.WriteSingle(baseline.CurrentX);
        writer.WriteSingle(baseline.CurrentZ);
        writer.WriteSingle(baseline.Auxiliary);
        writer.WriteInt64(baseline.ServerTimestamp.Ticks);
        writer.WriteUInt64(baseline.AcknowledgedInputId);
        writer.WriteUInt64(baseline.PositionRevision);
        writer.WriteUInt64(baseline.SimulationTick);
    }

    private static void WriteWorld(
        CanonicalWriter writer,
        in AuthoritativePlayerMovementWorldContext world)
    {
        writer.WriteUInt32(world.TransportEpoch);
        writer.WriteUInt32(world.WorldGeneration);
        writer.WriteByte(world.MapId);
        writer.WriteUInt32(world.SourceObjectId);
        writer.WriteBoolean(world.IsReady);
        writer.WriteBoolean(world.IsAlive);
        writer.WriteSingle(world.MovementMultiplier);
        writer.WriteByte((byte)world.AllowedSources);
    }

    private static void WriteDecision(
        CanonicalWriter writer,
        in AuthoritativePlayerMovementDecision decision)
    {
        writer.WriteBoolean(decision.Accepted);
        writer.WriteByte((byte)decision.RejectionReason);
        writer.WriteUInt64(decision.SimulationTick);
        writer.WriteUInt64(decision.Revision);
        writer.WriteUInt64(decision.InputId);
        writer.WriteUInt64(decision.AcknowledgedInputId);
        writer.WriteUInt32(decision.TransportEpoch);
        writer.WriteUInt32(decision.WorldGeneration);
        writer.WriteByte(decision.MapId);
        writer.WriteUInt32(decision.OpaqueState);
        writer.WriteSingle(decision.AuthoritativeX);
        writer.WriteSingle(decision.AuthoritativeZ);
        writer.WriteSingle(decision.AuthoritativeAuxiliary);
        writer.WriteByte((byte)decision.Source);
    }

    private static void WriteInput(
        CanonicalWriter writer,
        in AuthoritativePlayerMovementInput input)
    {
        writer.WriteUInt32(input.TransportEpoch);
        writer.WriteUInt64(input.InputId);
        writer.WriteUInt32(input.WorldGeneration);
        writer.WriteByte(input.MapId);
        writer.WriteUInt32(input.OpaqueState);
        writer.WriteSingle(input.TargetX);
        writer.WriteSingle(input.TargetZ);
        writer.WriteSingle(input.Auxiliary);
        writer.WriteUInt32(input.SourceObjectId);
        writer.WriteByte((byte)input.Source);
        writer.WriteBoolean(input.TargetsCurrentWorld);
    }

    private static void WriteSnapshot(
        CanonicalWriter writer,
        in AuthoritativePlayerMovementSnapshot snapshot)
    {
        writer.WriteUInt64(snapshot.SimulationTick);
        writer.WriteUInt64(snapshot.Revision);
        writer.WriteUInt64(snapshot.AcknowledgedInputId);
        writer.WriteUInt32(snapshot.TransportEpoch);
        writer.WriteUInt32(snapshot.WorldGeneration);
        writer.WriteByte(snapshot.MapId);
        writer.WriteUInt32(snapshot.OpaqueState);
        writer.WriteSingle(snapshot.AuthoritativeX);
        writer.WriteSingle(snapshot.AuthoritativeZ);
        writer.WriteSingle(snapshot.AuthoritativeAuxiliary);
    }

    private sealed class CanonicalWriter
    {
        private readonly MemoryStream _stream;

        public CanonicalWriter(int capacity)
        {
            _stream = new MemoryStream(capacity);
        }

        public void WriteByte(byte value) =>
            _stream.WriteByte(value);

        public void WriteBoolean(bool value) =>
            WriteByte(value ? (byte)1 : (byte)0);

        public void WriteBytes(ReadOnlySpan<byte> value) =>
            _stream.Write(value);

        public void WriteUInt16(ushort value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteInt32(int value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteUInt32(uint value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteInt64(long value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteUInt64(ulong value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(ulong)];
            BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
            WriteBytes(buffer);
        }

        public void WriteSingle(float value)
        {
            Span<byte> buffer = stackalloc byte[sizeof(float)];
            BinaryPrimitives.WriteInt32LittleEndian(
                buffer,
                BitConverter.SingleToInt32Bits(value));
            WriteBytes(buffer);
        }

        public byte[] ToArray() => _stream.ToArray();
    }
}
