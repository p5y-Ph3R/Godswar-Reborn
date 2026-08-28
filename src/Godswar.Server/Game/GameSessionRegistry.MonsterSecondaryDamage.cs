using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Commits terminal rebound/reflection damage only to the exact runtime
    /// and monster incarnation captured by the landed incoming hit. It never
    /// re-resolves by shared content-map ID.
    /// </summary>
    private bool TryApplyMonsterSecondaryDamageExact(
        WorldInstanceRuntime expectedRuntime,
        GameSessionContext source,
        MonsterRuntimeSnapshot expectedMonster,
        uint damage,
        int attackerCharacterId,
        DateTimeOffset committedAt,
        out MonsterDamageResult result)
    {
        ArgumentNullException.ThrowIfNull(expectedRuntime);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(expectedMonster);
        lock (_gate)
        {
            if (damage == 0 ||
                expectedMonster.RuntimeInstanceId == Guid.Empty ||
                expectedMonster.SpawnGeneration == 0 ||
                !TryResolvePlayerMonsterAuthorityLocked(
                    source.Session,
                    source.MapId,
                    out var current,
                    out var currentRuntime,
                    out _) ||
                current.CharacterId != attackerCharacterId ||
                current.WorldInstanceId != source.WorldInstanceId ||
                current.WorldRevision != source.WorldRevision ||
                current.Ownership != source.Ownership ||
                !ReferenceEquals(current.Character, source.Character) ||
                !ReferenceEquals(currentRuntime, expectedRuntime) ||
                current.WorldInstanceId != expectedRuntime.InstanceId ||
                current.MapId != expectedRuntime.MapId ||
                expectedMonster.Definition.MapId != expectedRuntime.MapId)
            {
                result = default!;
                return false;
            }

            var attempt = InvokeWorldOwnerAuthoritativeMutation(
                expectedRuntime,
                map =>
                {
                    MonsterDamageResult value = default!;
                    var applied = map.TryGetMonsterSnapshot(
                            expectedMonster.ObjectId,
                            out var currentMonster) &&
                        currentMonster.RuntimeInstanceId ==
                            expectedMonster.RuntimeInstanceId &&
                        currentMonster.SpawnGeneration ==
                            expectedMonster.SpawnGeneration &&
                        currentMonster.HealthRevision ==
                            expectedMonster.HealthRevision &&
                        map.TryApplyMonsterDamageGuarded(
                            expectedMonster.ObjectId,
                            damage,
                            attackerCharacterId,
                            expectedMonster.SpawnGeneration,
                            expectedMonster.HealthRevision,
                            committedAt,
                            out value);
                    return (Applied: applied, Value: value);
                });
            result = attempt.Value;
            return attempt.Applied;
        }
    }
}
