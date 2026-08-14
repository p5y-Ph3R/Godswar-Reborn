using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    // Published monsters currently have no authoritative healing producer.
    // Wither remains stored/fenced so a future producer must opt in here.
    internal const bool PveMonsterHealingProducerAvailable = false;

    internal CombatTargetStats AdjustPveMonsterTargetStats(
        ClientSession routingSession,
        MonsterRuntimeSnapshot target,
        DateTimeOffset authoritativeAt,
        in CombatTargetStats original)
    {
        if (!TryGetPveMonsterStatusForRead(
                routingSession,
                target,
                out var state))
        {
            return original;
        }

        lock (state.Gate)
        {
            var adjusted = state.Statuses.ApplyAdjustments(
                authoritativeAt.ToUnixTimeMilliseconds(),
                movementSpeed: 0,
                physicalDefense: Math.Max(
                    0,
                    original.PhysicalDefense),
                magicDefense: Math.Max(0, original.MagicDefense),
                hitRating: 0,
                healingReceived: 0);
            return original with
            {
                PhysicalDefense = ClampElementalCombatInt(
                    adjusted.PhysicalDefense),
                MagicDefense = ClampElementalCombatInt(
                    adjusted.MagicDefense)
            };
        }
    }

    internal MonsterCombatProfile AdjustPveMonsterAttackerProfile(
        ClientSession routingSession,
        MonsterRuntimeSnapshot source,
        DateTimeOffset authoritativeAt,
        in MonsterCombatProfile original)
    {
        if (!TryGetPveMonsterStatusForRead(
                routingSession,
                source,
                out var state))
        {
            return original;
        }

        lock (state.Gate)
        {
            var adjusted = state.Statuses.ApplyAdjustments(
                authoritativeAt.ToUnixTimeMilliseconds(),
                movementSpeed: 0,
                physicalDefense: 0,
                magicDefense: 0,
                hitRating: Math.Max(0, original.Hit),
                healingReceived: 0);
            return original with
            {
                Hit = ClampElementalCombatInt(adjusted.HitRating)
            };
        }
    }

    internal CombatResolution AdjustPveOutgoingResolution(
        ClientSession sourceSession,
        GameCharacter sourceCharacter,
        MonsterRuntimeSnapshot target,
        CombatEventProvenance provenance,
        DateTimeOffset authoritativeAt,
        in CombatResolution original,
        ulong momentumReservationScopeId = 0)
    {
        if (!original.Hit ||
            original.Damage == 0 ||
            original.EventId == 0 ||
            provenance is not (
                CombatEventProvenance.DirectBasicAttack or
                CombatEventProvenance.DirectSkill))
        {
            return original;
        }

        GameSessionContext source;
        ElementalCombatSessionState sourceState;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sourceSession, out source!) ||
                !source.WorldReady ||
                !ReferenceEquals(source.Character, sourceCharacter) ||
                source.CharacterId != sourceCharacter.Id ||
                source.MapId != target.Definition.MapId ||
                source.WorldInstanceId == default ||
                !IsCurrentAccountSession(
                    source.AccountId,
                    sourceSession,
                    source.Ownership) ||
                !TryGetElementalCombatSession(
                    sourceSession,
                    new ElementalCombatSessionFence(
                        source.CharacterId,
                        source.MapId,
                        source.Ownership),
                    out sourceState))
            {
                return original;
            }
        }

        lock (sourceState.Gate)
        {
            var combatEvent = new DeterministicCombatEventContext(
                original.EventId,
                source.MapId,
                source.CharacterId,
                target.ObjectId,
                authoritativeAt.ToUnixTimeMilliseconds(),
                provenance,
                Committed: false,
                IsPvp: false,
                default);
            var adjusted = ElementalResonanceExecutionPolicy
                .AdjustOutgoingDirectDamage(
                    combatEvent,
                    sourceCharacter.ElementalEquipment,
                    sourceState.Resonance,
                    original.Damage,
                    target.CurrentHealth,
                    target.MaximumHealth,
                    momentumReservationScopeId);
            return original with
            {
                Damage = checked((uint)Math.Clamp(
                    adjusted.AdjustedDamage,
                    0,
                    uint.MaxValue))
            };
        }
    }

    private bool TryGetPveMonsterStatusForRead(
        ClientSession routingSession,
        MonsterRuntimeSnapshot monster,
        out PveMonsterElementalState state)
    {
        state = null!;
        GameSessionContext route;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(routingSession, out route!) ||
                !route.WorldReady ||
                route.MapId != monster.Definition.MapId ||
                !IsCurrentAccountSession(
                    route.AccountId,
                    routingSession,
                    route.Ownership))
            {
                return false;
            }
        }

        var key = new PveMonsterElementalKey(
            route.WorldInstanceId,
            monster.ObjectId);
        return _pveMonsterElementalStates.TryGetValue(key, out state!) &&
            state.Identity.MapId == monster.Definition.MapId &&
            state.Identity.SpawnGeneration == monster.SpawnGeneration &&
            state.Identity.RuntimeInstanceId == monster.RuntimeInstanceId;
    }

    private static int ClampElementalCombatInt(long value) =>
        checked((int)Math.Clamp(value, 0, int.MaxValue));
}
