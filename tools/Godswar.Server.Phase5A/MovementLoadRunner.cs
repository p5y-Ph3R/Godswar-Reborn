using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Phase5A;

internal sealed class MovementLoadRunner
{
    private const uint TransportEpoch = 1;
    private const uint WorldGeneration = 17;
    private const byte MapId = 2;

    public async Task<Phase5AReport> RunAsync(
        Phase5AOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetStart = process.WorkingSet64;
        var handlesStart = process.HandleCount;
        var collectionsStart = CollectionCounts();
        var clock = Stopwatch.StartNew();

        var budget = new OperationBudget(options.PlannedOperations);
        var bots = CreateBots(options);
        var samples = new BoundedPercentileSampler(
            Phase5AOptions.PercentileSampleCapacity,
            options.Seed ^ 0xA5A5_5A5Au);
        using var digest =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendDigestHeader(digest, options);

        long completedTicks = 0;
        long accepted = 0;
        long rejected = 0;
        long tlsInputs = 0;
        long udpInputs = 0;
        long missedPacingDeadlines = 0;
        for (long tick = 1; tick <= options.PlannedTicks; tick++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.Mode == Phase5AMode.PacedSoak)
            {
                await PaceAsync(
                    clock,
                    tick - 1,
                    options.TickRate,
                    cancellationToken);
            }

            var tickStarted = Stopwatch.GetTimestamp();
            foreach (var bot in bots)
            {
                budget.Consume(Phase5AOptions.OperationsPerBotTick);
                ProcessBotTick(
                    bot,
                    checked((ulong)tick),
                    options,
                    digest,
                    ref accepted,
                    ref rejected,
                    ref tlsInputs,
                    ref udpInputs);
            }

            var tickEnded = Stopwatch.GetTimestamp();
            samples.Add(
                Stopwatch.GetElapsedTime(
                    tickStarted,
                    tickEnded).TotalMilliseconds);
            completedTicks++;
            if (options.Mode == Phase5AMode.PacedSoak &&
                clock.Elapsed >
                    TimeSpan.FromSeconds(
                        (double)tick / options.TickRate))
            {
                missedPacingDeadlines++;
            }
        }

        if (options.Mode == Phase5AMode.PacedSoak)
        {
            await PaceAsync(
                clock,
                completedTicks,
                options.TickRate,
                cancellationToken);
        }

        clock.Stop();
        process.Refresh();
        var cpuElapsed = process.TotalProcessorTime - cpuStart;
        var allocated = Math.Max(
            0,
            GC.GetTotalAllocatedBytes(precise: true) -
                allocationStart);
        var collectionsEnd = CollectionCounts();
        var completedBotTicks = checked(completedTicks * options.Bots);
        var inputBytes = checked(
            completedBotTicks *
            SecureRealtimeMovementProtocol.MovementInputBytes);
        var snapshotBytes = checked(
            completedBotTicks *
            SecureRealtimeMovementProtocol.PositionSnapshotBytes);
        var digestBytes = digest.GetHashAndReset();

        if (budget.Remaining != 0 ||
            rejected != 0 ||
            accepted + rejected != completedBotTicks)
        {
            throw new InvalidOperationException(
                "The completed workload did not match its validated budget.");
        }

        return new Phase5AReport(
            SchemaVersion: "reborn.phase5a.load-report.v1",
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Result: "passed",
            Configuration: Configuration(options),
            Environment: ReadEnvironment(),
            Resources: new Phase5AResources(
                clock.Elapsed.TotalMilliseconds,
                cpuElapsed.TotalMilliseconds,
                NormalizeCpu(cpuElapsed, clock.Elapsed),
                allocated,
                workingSetStart,
                process.WorkingSet64,
                process.PeakWorkingSet64,
                handlesStart,
                process.HandleCount,
                new Phase5AGarbageCollections(
                    collectionsEnd[0] - collectionsStart[0],
                    collectionsEnd[1] - collectionsStart[1],
                    collectionsEnd[2] - collectionsStart[2])),
            Packets: new Phase5APacketMetrics(
                completedBotTicks,
                tlsInputs,
                udpInputs,
                completedBotTicks,
                checked(completedBotTicks * 2),
                inputBytes,
                snapshotBytes,
                checked(inputBytes + snapshotBytes),
                PerSecond(
                    checked(completedBotTicks * 2),
                    clock.Elapsed),
                PerSecond(
                    checked(inputBytes + snapshotBytes),
                    clock.Elapsed)),
            Ticks: new Phase5ATickMetrics(
                options.PlannedTicks,
                completedTicks,
                completedBotTicks,
                accepted,
                rejected,
                AuthoritativePlayerMovementPolicy
                    .FixedStep.TotalMilliseconds,
                PerSecond(completedBotTicks, clock.Elapsed),
                missedPacingDeadlines,
                samples.Summarize()),
            Budget: new Phase5ABudgetMetrics(
                Phase5AOptions.MaximumTotalOperations,
                Phase5AOptions.DefaultTotalOperations,
                budget.Limit,
                budget.Consumed,
                budget.Remaining,
                "input encode + input decode + authority decision + " +
                "snapshot encode + snapshot decode + digest append"),
            Scope: new Phase5AScope(
                "Deterministic fixed-step authoritative movement codec and simulation workload.",
                "The exact prevalidated operation budget completed with all movement accepted and all codec round trips valid.",
                "Not exercised: ingress queues, admission control, load shedding, overload, and recovery require a separate bounded integration harness.",
                "No sockets, TLS handshakes, UDP protection, kernel networking, or configurable network target are exercised.",
                "Local synthetic baseline only; this is not a production capacity guarantee."),
            Digest: new Phase5ADigest(
                "SHA-256",
                Convert.ToHexString(digestBytes),
                "configuration plus ordered movement input and snapshot protocol bytes"));
    }

    private static void ProcessBotTick(
        BotState bot,
        ulong tick,
        Phase5AOptions options,
        IncrementalHash digest,
        ref long accepted,
        ref long rejected,
        ref long tlsInputs,
        ref long udpInputs)
    {
        var direction = bot.NextDirection();
        var before = bot.System.Snapshot;
        var source = tick % 10 == 0
            ? SecureRealtimeTransportSource.Tls
            : SecureRealtimeTransportSource.Udp;
        var flags = source == SecureRealtimeTransportSource.Tls
            ? SecureRealtimeMovementFlags.CurrentWorld
            : SecureRealtimeMovementFlags.None;
        var targetX = before.AuthoritativeX + direction.X;
        var targetZ = before.AuthoritativeZ + direction.Z;
        var input = new SecureRealtimeMovementInput(
            flags,
            TransportEpoch,
            tick,
            checked(tick * 50),
            WorldGeneration,
            bot.RandomState,
            targetX,
            targetZ,
            direction.Auxiliary,
            MapId);

        if (!SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                source,
                bot.InputPacket,
                out var inputBytes) ||
            !SecureRealtimeMovementProtocol.TryDecodeMovementInput(
                bot.InputPacket.AsSpan(0, inputBytes),
                source,
                out var decoded))
        {
            throw new InvalidOperationException(
                "The bounded workload failed its movement codec round trip.");
        }

        var authoritySource =
            source == SecureRealtimeTransportSource.Tls
                ? AuthoritativePlayerMovementSource.Tls
                : AuthoritativePlayerMovementSource.Udp;
        var decision = bot.System.ProcessLatest(
            new AuthoritativePlayerMovementInput(
                decoded.TransportEpoch,
                decoded.InputId,
                decoded.WorldGeneration,
                decoded.MapId,
                decoded.LegacyState,
                decoded.X,
                decoded.Z,
                decoded.Auxiliary,
                bot.SourceObjectId,
                authoritySource,
                TargetsCurrentWorld: true),
            bot.World,
            TimeSpan.FromTicks(
                checked(
                    (long)tick *
                    AuthoritativePlayerMovementPolicy.FixedStep.Ticks)));
        if (decision.Accepted)
        {
            accepted++;
        }
        else
        {
            rejected++;
        }

        if (source == SecureRealtimeTransportSource.Tls)
        {
            tlsInputs++;
        }
        else
        {
            udpInputs++;
        }

        var snapshot = new SecureRealtimePositionSnapshot(
            tick == 1 || tick % checked((ulong)options.TickRate) == 0
                ? SecureRealtimeSnapshotFlags.Keyframe
                : SecureRealtimeSnapshotFlags.None,
            decision.TransportEpoch,
            decision.AcknowledgedInputId,
            decision.SimulationTick,
            decision.Revision,
            tick,
            decision.WorldGeneration,
            decision.OpaqueState,
            decision.AuthoritativeX,
            decision.AuthoritativeZ,
            decision.AuthoritativeAuxiliary,
            decision.MapId,
            (SecureRealtimeMovementRejection)
                decision.RejectionReason);
        if (!SecureRealtimeMovementProtocol.TryEncodePositionSnapshot(
                snapshot,
                bot.SnapshotPacket,
                out var snapshotBytes) ||
            !SecureRealtimeMovementProtocol.TryDecodePositionSnapshot(
                bot.SnapshotPacket.AsSpan(0, snapshotBytes),
                out var decodedSnapshot))
        {
            throw new InvalidOperationException(
                "The bounded workload failed its snapshot codec round trip.");
        }

        BinaryPrimitives.WriteInt32BigEndian(
            bot.DigestPrefix,
            bot.Index);
        digest.AppendData(bot.DigestPrefix);
        digest.AppendData(bot.InputPacket.AsSpan(0, inputBytes));
        digest.AppendData(bot.SnapshotPacket.AsSpan(0, snapshotBytes));
        if (decodedSnapshot.ServerTick != tick)
        {
            throw new InvalidOperationException(
                "The snapshot lost its authoritative simulation tick.");
        }
    }

    private static BotState[] CreateBots(Phase5AOptions options)
    {
        var bots = new BotState[options.Bots];
        for (var index = 0; index < bots.Length; index++)
        {
            bots[index] = new BotState(index, options.Seed);
        }
        return bots;
    }

    private static async Task PaceAsync(
        Stopwatch clock,
        long completedTicks,
        int tickRate,
        CancellationToken cancellationToken)
    {
        var due = TimeSpan.FromSeconds(
            (double)completedTicks / tickRate);
        var remaining = due - clock.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, cancellationToken);
        }
    }

    private static Phase5AReportConfiguration Configuration(
        Phase5AOptions options) =>
        new(
            options.Mode == Phase5AMode.Load
                ? "load"
                : "paced-soak",
            "in-process-only; no sockets and no configurable target",
            options.Bots,
            options.DurationSeconds,
            options.TickRate,
            options.Seed,
            options.PlannedTicks,
            options.PlannedBotTicks,
            Phase5AOptions.OperationsPerBotTick,
            Phase5AOptions.PercentileSampleCapacity);

    private static Phase5AEnvironment ReadEnvironment() =>
        new(
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString());

    private static double NormalizeCpu(
        TimeSpan cpu,
        TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero
            ? 0d
            : cpu.TotalMilliseconds /
                elapsed.TotalMilliseconds /
                Environment.ProcessorCount *
                100d;

    private static double PerSecond(long count, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero
            ? 0d
            : count / elapsed.TotalSeconds;

    private static int[] CollectionCounts() =>
        [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)];

    private static void AppendDigestHeader(
        IncrementalHash digest,
        Phase5AOptions options)
    {
        Span<byte> header = stackalloc byte[25];
        BinaryPrimitives.WriteUInt32BigEndian(header, options.Seed);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], options.Bots);
        BinaryPrimitives.WriteInt32BigEndian(
            header[8..],
            options.DurationSeconds);
        BinaryPrimitives.WriteInt32BigEndian(
            header[12..],
            options.TickRate);
        BinaryPrimitives.WriteInt64BigEndian(
            header[16..],
            options.PlannedOperations);
        header[24] = options.Mode == Phase5AMode.Load
            ? (byte)1
            : (byte)2;
        digest.AppendData(header);
    }

    private sealed class BotState
    {
        private uint _randomState;

        public BotState(int index, uint rootSeed)
        {
            Index = index;
            SourceObjectId = checked((uint)index + 1);
            _randomState = MixSeed(rootSeed, checked((uint)index + 1));
            var x = (index % 32) * 2f;
            var z = (index / 32) * 2f;
            System = new AuthoritativePlayerMovementSystem(
                new AuthoritativePlayerMovementBaseline(
                    TransportEpoch,
                    WorldGeneration,
                    MapId,
                    SourceObjectId,
                    OpaqueState: 0,
                    CurrentX: x,
                    CurrentZ: z,
                    Auxiliary: 0f,
                    ServerTimestamp: TimeSpan.Zero));
            World = new AuthoritativePlayerMovementWorldContext(
                TransportEpoch,
                WorldGeneration,
                MapId,
                SourceObjectId,
                IsReady: true,
                IsAlive: true,
                MovementMultiplier: 1f,
                AuthoritativePlayerMovementSource.Tls |
                    AuthoritativePlayerMovementSource.Udp);
        }

        public int Index { get; }

        public uint SourceObjectId { get; }

        public uint RandomState => _randomState;

        public AuthoritativePlayerMovementSystem System { get; }

        public AuthoritativePlayerMovementWorldContext World { get; }

        public byte[] InputPacket { get; } =
            new byte[SecureRealtimeMovementProtocol.MovementInputBytes];

        public byte[] SnapshotPacket { get; } =
            new byte[SecureRealtimeMovementProtocol.PositionSnapshotBytes];

        public byte[] DigestPrefix { get; } = new byte[sizeof(int)];

        public Direction NextDirection()
        {
            _randomState ^= _randomState << 13;
            _randomState ^= _randomState >> 17;
            _randomState ^= _randomState << 5;
            return (_randomState & 3u) switch
            {
                0 => new Direction(0.1f, 0f, 0f),
                1 => new Direction(-0.1f, 0f, 1f),
                2 => new Direction(0f, 0.1f, 2f),
                _ => new Direction(0f, -0.1f, 3f)
            };
        }

        private static uint MixSeed(uint root, uint stream)
        {
            var mixed = root ^ (stream * 0x9E37_79B9u);
            mixed ^= mixed >> 16;
            mixed *= 0x7FEB_352Du;
            mixed ^= mixed >> 15;
            mixed *= 0x846C_A68Bu;
            mixed ^= mixed >> 16;
            return mixed == 0 ? 1u : mixed;
        }
    }

    private readonly record struct Direction(
        float X,
        float Z,
        float Auxiliary);
}
