using Godswar.Server.Ecs;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct MonsterPlayerDamageAppliedEvent(
    ulong DecisionSequence,
    EntityId Player,
    ulong AttackEventId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    uint RequestedDamage,
    uint AppliedDamage,
    int BeforeHealth,
    int AfterHealth,
    long BeforeVitalsRevision,
    long AfterVitalsRevision,
    long BeforeLifeRevision,
    long AfterLifeRevision,
    bool Killed);

internal readonly record struct MonsterPlayerDamageRejectedEvent(
    ulong DecisionSequence,
    EntityId Player,
    ulong AttackEventId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    MonsterPlayerDamageRejectionReason Reason,
    int CurrentHealth,
    long CurrentVitalsRevision,
    long CurrentLifeRevision,
    ulong LastAttackEventId);

internal readonly record struct MonsterPlayerDeathDecisionEvent(
    ulong DecisionSequence,
    EntityId Player,
    ulong AttackEventId,
    uint MonsterObjectId,
    int CharacterId,
    uint PlayerObjectId,
    long BeforeLifeRevision,
    long AfterLifeRevision,
    long VitalsRevision);
