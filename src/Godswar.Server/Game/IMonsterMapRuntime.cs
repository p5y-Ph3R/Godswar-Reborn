using Godswar.Server.State;

namespace Godswar.Server.Game;

internal interface IMonsterMapRuntime
{
    byte MapId { get; }

    int Count { get; }

    IReadOnlyList<MonsterRuntimeSnapshot> Snapshot();

    bool TryGetSnapshot(
        uint objectId,
        out MonsterRuntimeSnapshot snapshot);

    bool TryApplyDamage(
        uint objectId,
        uint damage,
        int? attackerCharacterId,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterDamageResult result);

    bool TryApplyStun(
        uint objectId,
        int attackerCharacterId,
        TimeSpan duration,
        uint? expectedSpawnGeneration,
        DateTimeOffset now,
        out MonsterStunResult result);

    void ClearAggroForCharacter(
        int characterId,
        DateTimeOffset now);

    MonsterRuntimeTick Advance(
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget>? combatTargets = null);
}
