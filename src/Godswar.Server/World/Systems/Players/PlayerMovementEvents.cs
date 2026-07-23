using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

internal readonly record struct PlayerMovementProjectedEvent(
    EntityId Entity,
    ulong IntentSequence,
    ulong ProjectionRevision,
    byte MapId,
    float PreviousX,
    float PreviousZ,
    float TargetX,
    float TargetZ,
    float CurrentX,
    float CurrentZ);

internal readonly record struct PlayerMovementRejectedEvent(
    EntityId Entity,
    ulong IntentSequence,
    ulong ProjectionRevision,
    PlayerMovementRejectionReason Reason,
    byte MapId,
    float TargetX,
    float TargetZ,
    float CurrentX,
    float CurrentZ);
