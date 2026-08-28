using System.Runtime.CompilerServices;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConditionalWeakTable<
        MonsterRuntimeUpdate,
        MonsterAttackEventIdentity> _monsterAttackEventIdentities =
        new();
    private readonly object _monsterAttackEventIdGate = new();
    private ulong _nextMonsterAttackEventId;

    private ulong ResolveMonsterAttackEventId(
        MonsterRuntimeUpdate attack)
    {
        if (attack.AttackEventId != 0)
        {
            ObserveMonsterAttackEventId(attack.AttackEventId);
            return attack.AttackEventId;
        }

        return _monsterAttackEventIdentities.GetValue(
            attack,
            _ => new MonsterAttackEventIdentity(
                AllocateRequiredMonsterAttackEventIdAbove(0)))
            .Value;
    }

    private void ObserveMonsterAttackEventId(ulong eventId)
    {
        if (eventId == 0)
        {
            return;
        }

        lock (_monsterAttackEventIdGate)
        {
            if (eventId > _nextMonsterAttackEventId)
            {
                _nextMonsterAttackEventId = eventId;
            }
        }
    }

    private bool TryAllocateMonsterAttackEventIdAbove(
        ulong floor,
        out ulong eventId)
    {
        lock (_monsterAttackEventIdGate)
        {
            var current = Math.Max(
                floor,
                _nextMonsterAttackEventId);
            _nextMonsterAttackEventId = current;
            if (current == ulong.MaxValue)
            {
                eventId = 0;
                return false;
            }

            eventId = current + 1;
            _nextMonsterAttackEventId = eventId;
            return true;
        }
    }

    private ulong AllocateRequiredMonsterAttackEventIdAbove(
        ulong floor)
    {
        if (TryAllocateMonsterAttackEventIdAbove(
                floor,
                out var eventId))
        {
            return eventId;
        }

        throw new InvalidOperationException(
            "The monster-attack event identity space is exhausted.");
    }
}
