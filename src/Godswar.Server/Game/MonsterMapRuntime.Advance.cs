namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
{
    public MonsterRuntimeTick Advance(
        DateTimeOffset now,
        IReadOnlyList<MonsterCombatTarget>? combatTargets = null)
    {
        lock (_gate)
        {
            var targetsByCharacterId = (combatTargets ?? [])
                .GroupBy(target => target.CharacterId)
                .ToDictionary(group => group.Key, group => group.Last());
            var updates = new List<MonsterRuntimeUpdate>(_pendingUpdates.Count + 4);
            var deathsAnnouncedThisTick = new HashSet<uint>();
            var returnStartsAnnouncedThisTick = new HashSet<uint>();
            while (_pendingUpdates.TryDequeue(out var pendingUpdate))
            {
                updates.Add(pendingUpdate);
                if (pendingUpdate.Kind == MonsterRuntimeUpdateKind.Died)
                {
                    deathsAnnouncedThisTick.Add(pendingUpdate.Monster.ObjectId);
                }
                else if (pendingUpdate.Kind == MonsterRuntimeUpdateKind.Started &&
                         pendingUpdate.Monster.CombatPhase == MonsterCombatPhase.Returning)
                {
                    returnStartsAnnouncedThisTick.Add(pendingUpdate.Monster.ObjectId);
                }
            }

            var positionsChanged = false;
            List<MonsterRuntimeState>? respawnedStates = null;
            foreach (var monster in _monsters.Values.OrderBy(monster => monster.Definition.ObjectId))
            {
                if (!monster.IsAlive)
                {
                    if (deathsAnnouncedThisTick.Contains(monster.Definition.ObjectId))
                    {
                        continue;
                    }

                    if (monster.IsSpawned &&
                        monster.DespawnAt is { } despawnAt &&
                        now >= despawnAt)
                    {
                        monster.IsSpawned = false;
                        updates.Add(new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Despawned,
                            CreateSnapshot(monster)));
                        continue;
                    }

                    if (!monster.IsSpawned &&
                        monster.RespawnAt is { } respawnAt &&
                        now >= respawnAt)
                    {
                        var respawned = CreateRespawnedState(monster, now);
                        (respawnedStates ??= []).Add(respawned);
                        positionsChanged = true;
                        updates.Add(new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Respawned,
                            CreateSnapshot(respawned)));
                    }

                    continue;
                }

                if (monster.StunnedUntil is { } stunnedUntil)
                {
                    if (now < stunnedUntil)
                    {
                        continue;
                    }

                    monster.StunnedUntil = null;
                    monster.NextAttackAt = now + TickInterval;
                    monster.NextMovementStepAt = now + TickInterval;
                }

                if (monster.AggroCharacterId is { } aggroCharacterId)
                {
                    if (targetsByCharacterId.TryGetValue(aggroCharacterId, out var combatTarget) &&
                        combatTarget.IsAlive &&
                        DistanceSquared(monster.HomeX, monster.HomeZ, combatTarget.X, combatTarget.Z) <=
                        (CombatLeashRadius + CombatRange) * (CombatLeashRadius + CombatRange))
                    {
                        positionsChanged |= AdvanceCombat(monster, combatTarget, now, updates);
                        continue;
                    }

                    AddReturnStart(monster, now, updates);
                    continue;
                }

                if (monster.CombatPhase == MonsterCombatPhase.Returning)
                {
                    if (!returnStartsAnnouncedThisTick.Contains(monster.Definition.ObjectId))
                    {
                        positionsChanged |= AdvanceReturnHome(monster, now, updates);
                    }

                    continue;
                }

                if (monster.CombatPhase == MonsterCombatPhase.AwaitingRetirement)
                {
                    updates.Add(RetireReturnedMonster(monster, now));
                    continue;
                }

                if (monster.IsMoving)
                {
                    while (monster.IsMoving && now >= monster.NextMovementStepAt)
                    {
                        var stepAt = monster.NextMovementStepAt;
                        monster.CurrentX += monster.VelocityX;
                        monster.CurrentZ += monster.VelocityZ;
                        monster.RemainingMovementTicks--;
                        monster.NextMovementStepAt += TickInterval;
                        positionsChanged = true;

                        if (monster.RemainingMovementTicks != 0)
                        {
                            continue;
                        }

                        // Pin the final coordinates to the accepted target so float
                        // accumulation can never drift beyond the home-radius bound.
                        monster.CurrentX = monster.TargetX;
                        monster.CurrentZ = monster.TargetZ;
                        monster.IsMoving = false;
                        monster.NextMovementAt = stepAt + NextIdleDelay(monster);
                        updates.Add(new MonsterRuntimeUpdate(
                            MonsterRuntimeUpdateKind.Arrived,
                            CreateSnapshot(monster)));
                    }

                    continue;
                }

                if (now < monster.NextMovementAt)
                {
                    continue;
                }

                StartMovement(monster, now);
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Started,
                    CreateSnapshot(monster)));
            }

            if (respawnedStates is not null)
            {
                foreach (var respawned in respawnedStates)
                {
                    _monsters[respawned.Definition.ObjectId] = respawned;
                }
            }

            return new MonsterRuntimeTick(positionsChanged, updates);
        }
    }
}
