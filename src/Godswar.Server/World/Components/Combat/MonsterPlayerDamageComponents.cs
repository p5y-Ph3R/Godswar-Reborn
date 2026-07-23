using Godswar.Server.Ecs;

namespace Godswar.Server.World.Components.Combat;

internal enum MonsterPlayerDamageRejectionReason : byte
{
    None = 0,
    IdentityMismatch = 1,
    LifeRevisionMismatch = 2,
    DuplicateAttackEvent = 3,
    StaleAttackEvent = 4,
    VitalsRevisionMismatch = 5,
    PlayerAlreadyDead = 6,
    ZeroDamage = 7
}

/// <summary>
/// Player-life identity and monster-attack deduplication state. A zero attack
/// event ID means the upstream runtime did not provide a stable identity.
/// </summary>
internal struct MonsterPlayerDamageStateComponent
{
    public MonsterPlayerDamageStateComponent(
        long lifeRevision,
        ulong lastAttackEventId,
        ulong decisionSequence)
    {
        LifeRevision = lifeRevision;
        LastAttackEventId = lastAttackEventId;
        DecisionSequence = decisionSequence;
    }

    public long LifeRevision;
    public ulong LastAttackEventId;
    public ulong DecisionSequence;
}

/// <summary>
/// Immutable scalar attack decision supplied by the live registry adapter.
/// Damage has already passed through the server's defense and Holy Ward rules.
/// </summary>
internal readonly record struct MonsterPlayerDamageIntentComponent(
    ulong AttackEventId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    int ExpectedCharacterId,
    uint ExpectedPlayerObjectId,
    long ExpectedLifeRevision,
    long ExpectedVitalsRevision,
    uint ResolvedDamage);

internal readonly record struct MonsterPlayerDamageHydrationSnapshot(
    int CharacterId,
    int AccountId,
    uint PlayerObjectId,
    int CurrentHp,
    int MaximumHp,
    int CurrentMp,
    int MaximumMp,
    long VitalsRevision,
    long LifeRevision);

internal readonly record struct MonsterPlayerDamageEntity(
    EntityId Entity);
