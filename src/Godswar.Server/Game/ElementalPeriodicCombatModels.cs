using Godswar.Server.World.Systems.Combat;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Game;

internal sealed record PlayerElementalBurnCommit(
    GameSessionContext Target,
    GameSessionContext? Source,
    int SourceCharacterId,
    ulong SourceEventId,
    int TickOrdinal,
    uint AppliedDamage,
    bool Killed,
    long DeathLifeRevision,
    bool SourceRecoveryApplied,
    Task DeathInterruption);

internal sealed record MonsterElementalBurnCommit(
    WorldInstanceId WorldInstanceId,
    int SourceCharacterId,
    GameSessionContext? Source,
    GameSessionContext? RoutingContext,
    ResonanceDamageIntent Intent,
    MonsterDamageResult DamageResult,
    PveElementalSourceRecoveryCommit SourceRecovery);
