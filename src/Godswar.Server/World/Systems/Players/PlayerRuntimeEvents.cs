using System.Collections.Immutable;
using Godswar.Server.Ecs;
using Godswar.Server.State;
using Godswar.Server.World.Components.Players;

namespace Godswar.Server.World.Systems.Players;

internal readonly record struct PlayerRuntimeClockAdvancedEvent(
    EntityId Entity,
    DateTimeOffset PreviousAt,
    DateTimeOffset CurrentAt);

internal readonly record struct PlayerVitalsRecoveredEvent(
    EntityId Entity,
    DateTimeOffset PulseAt,
    int PreviousHp,
    int CurrentHp,
    int PreviousMp,
    int CurrentMp,
    long VitalsRevision);

internal readonly record struct PlayerRuntimeStatusExpiredEvent(
    EntityId Entity,
    uint StatusId,
    int Kind,
    DateTimeOffset ExpiresAt,
    long Revision);

internal readonly record struct PlayerStatusCompositionChangedEvent(
    EntityId Entity,
    DateTimeOffset ComposedAt,
    ImmutableArray<PlayerComposedStatusEffect> Effects,
    ClientStatusAggregate Aggregate,
    string Fingerprint);

internal readonly record struct PlayerOnlineDurationAccountedEvent(
    EntityId Entity,
    int AccountId,
    int CharacterId,
    PlayerOnlineDurationTarget Target,
    DateTimeOffset OnlineFrom,
    DateTimeOffset OnlineUntil,
    long ElapsedTicks,
    long TotalElapsedTicks);
