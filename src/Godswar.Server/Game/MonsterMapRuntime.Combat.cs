namespace Godswar.Server.Game;

internal sealed partial class MonsterMapRuntime
{
    private static bool AdvanceCombat(
        MonsterRuntimeState monster,
        MonsterCombatTarget target,
        DateTimeOffset now,
        List<MonsterRuntimeUpdate> updates)
    {
        var positionsChanged = false;
        var distance = Math.Sqrt(DistanceSquared(monster.CurrentX, monster.CurrentZ, target.X, target.Z));
        if (distance <= monster.AttackRange)
        {
            if (monster.CombatPhase == MonsterCombatPhase.Chasing || monster.IsMoving)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = now + TickInterval;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                return false;
            }

            if (monster.CombatPhase != MonsterCombatPhase.Attacking)
            {
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = now + TickInterval;
                return false;
            }

            if (now >= monster.NextAttackAt)
            {
                monster.NextAttackAt = now + AttackCooldown;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Attacked,
                    CreateSnapshot(monster),
                    TargetCharacterId: target.CharacterId,
                    TargetX: target.X,
                    TargetZ: target.Z,
                    TargetObjectId: target.ObjectId == 0
                        ? null
                        : target.ObjectId,
                    TargetLifeRevision: target.ObjectId == 0
                        ? null
                        : target.LifeRevision,
                    TargetOwnership: target.ObjectId == 0
                        ? null
                        : target.Ownership,
                    TargetWorldInstanceId: target.ObjectId == 0
                        ? null
                        : target.WorldInstanceId,
                    TargetWorldRevision: target.ObjectId == 0
                        ? null
                        : target.WorldRevision,
                    TargetWorldMembershipEpoch: target.ObjectId == 0
                        ? null
                        : target.WorldMembershipEpoch));
            }

            return false;
        }

        if (monster.CombatPhase != MonsterCombatPhase.Chasing)
        {
            monster.CombatPhase = MonsterCombatPhase.Chasing;
            monster.HasSentInitialChase = true;
            monster.IsMoving = true;
            monster.MovementTicks = 1;
            monster.RemainingMovementTicks = 1;
            SetCombatVelocity(monster, target);
            monster.NextMovementStepAt =
                now + ElementalMovementInterval(monster);
            updates.Add(new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Started,
                CreateSnapshot(monster),
                MovementMode: 0));
            return false;
        }

        while (now >= monster.NextMovementStepAt)
        {
            var stepAt = monster.NextMovementStepAt;
            distance = Math.Sqrt(DistanceSquared(monster.CurrentX, monster.CurrentZ, target.X, target.Z));
            var remainingDistance = Math.Max(
                0d,
                distance - monster.AttackRange);
            if (remainingDistance <= double.Epsilon)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = stepAt + TickInterval;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                break;
            }

            SetCombatVelocity(monster, target, Math.Min(MovementStep, (float)remainingDistance));
            var nextX = monster.CurrentX + monster.VelocityX;
            var nextZ = monster.CurrentZ + monster.VelocityZ;
            if (DistanceSquared(monster.HomeX, monster.HomeZ, nextX, nextZ) >
                CombatLeashRadius * CombatLeashRadius)
            {
                AddReturnStart(monster, stepAt, updates);
                break;
            }

            monster.CurrentX = nextX;
            monster.CurrentZ = nextZ;
            monster.NextMovementStepAt +=
                ElementalMovementInterval(monster);
            positionsChanged = true;

            distance = Math.Sqrt(DistanceSquared(monster.CurrentX, monster.CurrentZ, target.X, target.Z));
            if (distance <= monster.AttackRange + 0.0001d)
            {
                StopCombatMovement(monster);
                monster.CombatPhase = MonsterCombatPhase.Attacking;
                monster.NextAttackAt = stepAt + TickInterval;
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Arrived,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                break;
            }

            SetCombatVelocity(monster, target);
            updates.Add(new MonsterRuntimeUpdate(
                MonsterRuntimeUpdateKind.Started,
                CreateSnapshot(monster),
                MovementMode: 1));
        }

        return positionsChanged;
    }

    private static bool AdvanceReturnHome(
        MonsterRuntimeState monster,
        DateTimeOffset now,
        List<MonsterRuntimeUpdate> updates)
    {
        var positionsChanged = false;
        while (monster.CombatPhase == MonsterCombatPhase.Returning &&
               now >= monster.NextMovementStepAt)
        {
            var stepAt = monster.NextMovementStepAt;
            if (monster.RemainingMovementTicks <= 1)
            {
                positionsChanged |= DistanceSquared(
                    monster.CurrentX,
                    monster.CurrentZ,
                    monster.HomeX,
                    monster.HomeZ) > double.Epsilon;
                CompleteReturnHome(monster, stepAt);
                updates.Add(new MonsterRuntimeUpdate(
                    MonsterRuntimeUpdateKind.Returned,
                    CreateSnapshot(monster),
                    MovementEndField: 1));
                if (ShouldRetireReturnedMonster(monster))
                {
                    updates.Add(RetireReturnedMonster(monster, stepAt));
                }

                break;
            }

            monster.CurrentX += monster.VelocityX;
            monster.CurrentZ += monster.VelocityZ;
            monster.RemainingMovementTicks--;
            monster.NextMovementStepAt +=
                ElementalMovementInterval(monster);
            positionsChanged = true;
        }

        return positionsChanged;
    }

    private static void SetCombatVelocity(
        MonsterRuntimeState monster,
        MonsterCombatTarget target,
        float step = MovementStep)
    {
        var deltaX = target.X - monster.CurrentX;
        var deltaZ = target.Z - monster.CurrentZ;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= double.Epsilon)
        {
            monster.VelocityX = 0;
            monster.VelocityZ = 0;
            return;
        }

        monster.VelocityX = (float)((deltaX / distance) * step);
        monster.VelocityZ = (float)((deltaZ / distance) * step);
        monster.Facing = MathF.Atan2(monster.VelocityX, monster.VelocityZ);
    }

    private static void StopCombatMovement(MonsterRuntimeState monster)
    {
        monster.IsMoving = false;
        monster.VelocityX = 0;
        monster.VelocityZ = 0;
        monster.MovementTicks = 1;
        monster.RemainingMovementTicks = 0;
    }

    private static bool SetAggroTarget(
        MonsterRuntimeState monster,
        int characterId,
        DateTimeOffset now)
    {
        var stoppedMovement = monster.IsMoving;
        monster.AggroCharacterId = characterId;
        monster.CombatPhase = MonsterCombatPhase.None;
        monster.HasSentInitialChase = false;
        StopCombatMovement(monster);
        monster.MovementTicks = 0;
        monster.NextAttackAt = now + TickInterval;
        return stoppedMovement;
    }

    private static MonsterRuntimeUpdate BeginReturnHome(
        MonsterRuntimeState monster,
        DateTimeOffset now)
    {
        var returnedImmediately = BeginReturnHomeState(monster, now);
        return new MonsterRuntimeUpdate(
            returnedImmediately
                ? MonsterRuntimeUpdateKind.Returned
                : MonsterRuntimeUpdateKind.Started,
            CreateSnapshot(monster),
            MovementMode: returnedImmediately ? 1u : 0u,
            MovementEndField: returnedImmediately ? 1u : null);
    }

    private static bool BeginReturnHomeState(
        MonsterRuntimeState monster,
        DateTimeOffset now)
    {
        monster.StunnedUntil = null;
        monster.AggroCharacterId = null;
        monster.FirstHitCharacterId = null;
        monster.DamageThreat.Clear();
        monster.HasSentInitialChase = false;
        monster.NextAttackAt = default;
        monster.DespawnAt = null;
        monster.RespawnAt = null;
        var deltaX = monster.HomeX - monster.CurrentX;
        var deltaZ = monster.HomeZ - monster.CurrentZ;
        var distance = Math.Sqrt((deltaX * deltaX) + (deltaZ * deltaZ));
        if (distance <= 0.0001d)
        {
            CompleteReturnHome(monster, now);
            return true;
        }

        monster.CombatPhase = MonsterCombatPhase.Returning;
        var movementTicks = Math.Max(1, checked((int)Math.Ceiling(distance / MovementStep)));
        var movementStep = distance / movementTicks;
        SetMovement(
            monster,
            now,
            movementTicks,
            (float)((deltaX / distance) * movementStep),
            (float)((deltaZ / distance) * movementStep),
            monster.HomeX,
            monster.HomeZ);
        return false;
    }

    private static void AddReturnStart(
        MonsterRuntimeState monster,
        DateTimeOffset now,
        List<MonsterRuntimeUpdate> updates)
    {
        var returnUpdate = BeginReturnHome(monster, now);
        updates.Add(returnUpdate);
        if (returnUpdate.Kind == MonsterRuntimeUpdateKind.Returned &&
            ShouldRetireReturnedMonster(monster))
        {
            updates.Add(RetireReturnedMonster(monster, now));
        }
    }

    private static void CompleteReturnHome(MonsterRuntimeState monster, DateTimeOffset now)
    {
        monster.StunnedUntil = null;
        monster.AggroCharacterId = null;
        monster.FirstHitCharacterId = null;
        monster.DamageThreat.Clear();
        monster.CombatPhase = MonsterCombatPhase.AwaitingRetirement;
        monster.HasSentInitialChase = false;
        monster.NextAttackAt = default;
        monster.CurrentX = monster.HomeX;
        monster.CurrentZ = monster.HomeZ;
        monster.Facing = monster.HomeFacing;
        StopCombatMovement(monster);
        monster.MovementTicks = 0;
        monster.TargetX = monster.HomeX;
        monster.TargetZ = monster.HomeZ;
        monster.NextMovementAt = now + NextIdleDelay(monster);
    }

    private static MonsterRuntimeUpdate RetireReturnedMonster(
        MonsterRuntimeState monster,
        DateTimeOffset now)
    {
        if (monster.RespawnPolicy != MonsterRespawnPolicy.Timed)
        {
            throw new InvalidOperationException(
                "Only timed monster lifecycles retire for replacement.");
        }

        // Keep the damaged entity visible through its immutable exact-home
        // Returned snapshot, then retire it later in the same ordered update
        // batch. The following world tick publishes a new full-health runtime
        // generation through the normal spawn path.
        monster.IsAlive = false;
        monster.IsSpawned = false;
        monster.DespawnAt = null;
        monster.RespawnAt = now + TickInterval;
        return new MonsterRuntimeUpdate(
            MonsterRuntimeUpdateKind.Despawned,
            CreateSnapshot(monster));
    }

    private static bool ShouldRetireReturnedMonster(
        MonsterRuntimeState monster) =>
        monster.RespawnPolicy switch
        {
            MonsterRespawnPolicy.Timed => true,
            MonsterRespawnPolicy.Never => false,
            _ => throw new InvalidOperationException(
                "Monster state contains an unsupported respawn policy.")
        };

    private static void SettleReturnedMonster(MonsterRuntimeState monster)
    {
        if (monster.RespawnPolicy != MonsterRespawnPolicy.Never ||
            monster.CombatPhase != MonsterCombatPhase.AwaitingRetirement)
        {
            throw new InvalidOperationException(
                "Only a returned never-respawn monster can settle in place.");
        }

        monster.CombatPhase = MonsterCombatPhase.None;
    }

    private static void ResetCombat(MonsterRuntimeState monster, DateTimeOffset now)
    {
        monster.StunnedUntil = null;
        monster.AggroCharacterId = null;
        monster.FirstHitCharacterId = null;
        monster.DamageThreat.Clear();
        monster.CombatPhase = MonsterCombatPhase.None;
        monster.HasSentInitialChase = false;
        StopCombatMovement(monster);
        monster.MovementTicks = 0;
        monster.NextAttackAt = default;
        monster.NextMovementAt = now + NextIdleDelay(monster);
    }
}
