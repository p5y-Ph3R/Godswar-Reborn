namespace Godswar.Server.World.Systems.Players;

[Flags]
internal enum AuthoritativePlayerMovementSource : byte
{
    None = 0,
    Tls = 1 << 0,
    Udp = 1 << 1
}

internal enum AuthoritativePlayerMovementRejectionReason : byte
{
    None = 0,
    Malformed = 1,
    NotReady = 2,
    Dead = 3,
    InvalidCoordinates = 4,
    MapTransition = 5,
    Cadence = 6,
    Speed = 7,
    Distance = 8,
    StaleInput = 9,
    TransportEpoch = 10,
    TransportSource = 11,
    Overloaded = 12
}

/// <summary>
/// A movement intent after transport authentication and session lookup.
/// It intentionally contains no client-authored clock or elapsed duration.
/// </summary>
internal readonly record struct AuthoritativePlayerMovementInput(
    uint TransportEpoch,
    ulong InputId,
    uint WorldGeneration,
    byte MapId,
    uint OpaqueState,
    float TargetX,
    float TargetZ,
    float Auxiliary,
    uint SourceObjectId,
    AuthoritativePlayerMovementSource Source,
    bool TargetsCurrentWorld);

/// <summary>
/// Server-owned world facts supplied by the single owner of the player.
/// </summary>
internal readonly record struct AuthoritativePlayerMovementWorldContext(
    uint TransportEpoch,
    uint WorldGeneration,
    byte MapId,
    uint SourceObjectId,
    bool IsReady,
    bool IsAlive,
    float MovementMultiplier,
    AuthoritativePlayerMovementSource AllowedSources);

internal readonly record struct AuthoritativePlayerMovementBaseline(
    uint TransportEpoch,
    uint WorldGeneration,
    byte MapId,
    uint SourceObjectId,
    uint OpaqueState,
    float CurrentX,
    float CurrentZ,
    float Auxiliary,
    TimeSpan ServerTimestamp,
    ulong AcknowledgedInputId = 0,
    ulong PositionRevision = 0,
    ulong SimulationTick = 0);

internal readonly record struct AuthoritativePlayerMovementDecision(
    bool Accepted,
    AuthoritativePlayerMovementRejectionReason RejectionReason,
    ulong SimulationTick,
    ulong Revision,
    ulong InputId,
    ulong AcknowledgedInputId,
    uint TransportEpoch,
    uint WorldGeneration,
    byte MapId,
    uint OpaqueState,
    float AuthoritativeX,
    float AuthoritativeZ,
    float AuthoritativeAuxiliary,
    AuthoritativePlayerMovementSource Source);

internal readonly record struct AuthoritativePlayerMovementSnapshot(
    ulong SimulationTick,
    ulong Revision,
    ulong AcknowledgedInputId,
    uint TransportEpoch,
    uint WorldGeneration,
    byte MapId,
    uint OpaqueState,
    float AuthoritativeX,
    float AuthoritativeZ,
    float AuthoritativeAuxiliary);
