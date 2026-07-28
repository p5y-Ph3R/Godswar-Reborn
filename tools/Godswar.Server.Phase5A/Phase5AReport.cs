namespace Godswar.Server.Phase5A;

internal sealed record Phase5AReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string Result,
    Phase5AReportConfiguration Configuration,
    Phase5AEnvironment Environment,
    Phase5AResources Resources,
    Phase5APacketMetrics Packets,
    Phase5ATickMetrics Ticks,
    Phase5ABudgetMetrics Budget,
    Phase5AScope Scope,
    Phase5ADigest Digest);

internal sealed record Phase5AReportConfiguration(
    string Mode,
    string TrafficBoundary,
    int Bots,
    int LogicalDurationSeconds,
    int TickRate,
    uint Seed,
    long PlannedTicks,
    long PlannedBotTicks,
    int OperationsPerBotTick,
    int PercentileSampleCapacity);

internal sealed record Phase5AEnvironment(
    string OperatingSystem,
    string Framework,
    string ProcessArchitecture,
    string OperatingSystemArchitecture,
    int ProcessorCount,
    bool ServerGarbageCollection,
    string GarbageCollectionLatencyMode);

internal sealed record Phase5AResources(
    double ElapsedMilliseconds,
    double ProcessCpuMilliseconds,
    double NormalizedCpuPercent,
    long AllocatedBytes,
    long WorkingSetStartBytes,
    long WorkingSetEndBytes,
    long PeakWorkingSetBytes,
    int ProcessHandlesStart,
    int ProcessHandlesEnd,
    Phase5AGarbageCollections GarbageCollections);

internal sealed record Phase5AGarbageCollections(
    int Generation0,
    int Generation1,
    int Generation2);

internal sealed record Phase5APacketMetrics(
    long InputPackets,
    long TlsInputPackets,
    long UdpInputPackets,
    long SnapshotPackets,
    long TotalPackets,
    long InputBytes,
    long SnapshotBytes,
    long TotalBytes,
    double PacketsPerSecond,
    double BytesPerSecond);

internal sealed record Phase5ATickMetrics(
    long PlannedTicks,
    long CompletedTicks,
    long CompletedBotTicks,
    long AcceptedMovements,
    long RejectedMovements,
    double FixedStepMilliseconds,
    double BotTicksPerSecond,
    long MissedPacingDeadlines,
    PercentileSummary ProcessingDuration);

internal sealed record Phase5ABudgetMetrics(
    long HardOperationCap,
    long DefaultRunOperations,
    long RunLimit,
    long Consumed,
    long Remaining,
    string OperationDefinition);

internal sealed record Phase5AScope(
    string Workload,
    string PassMeaning,
    string OverloadRecoveryCoverage,
    string NetworkCoverage,
    string CapacityClaim);

internal sealed record Phase5ADigest(
    string Algorithm,
    string Value,
    string Scope);

internal sealed record Phase5ASelfCheckReport(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    string Result,
    int Checks,
    string RepeatableDigest,
    string DifferentSeedDigest,
    string TrafficBoundary);
