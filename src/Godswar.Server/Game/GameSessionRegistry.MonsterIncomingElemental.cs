using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct MonsterIncomingElementalAttempt(
    DeterministicCombatEventContext CombatEvent,
    IncomingResonanceAdjustment Adjustment);

internal readonly record struct MonsterIncomingElementalPostCommit(
    int BeforeHealth,
    int AfterHealth,
    int BeforeMana,
    int AfterMana,
    long BeforeVitalsRevision,
    long AfterVitalsRevision,
    ResonanceDamageIntent? Reflection)
{
    public bool RecoveryApplied =>
        BeforeHealth != AfterHealth || BeforeMana != AfterMana;
}

internal sealed partial class GameSessionRegistry
{
    private readonly MonsterIncomingAttackReplayLedger
        _monsterIncomingAttackReplay = new();

    private bool TryClaimMonsterIncomingAttack(
        GameSessionContext target,
        MonsterRuntimeSnapshot monster,
        ulong combatEventId) =>
        _monsterIncomingAttackReplay.TryClaim(new(
            MonsterIncomingAttackCommitPhase.DirectAttack,
            target.CharacterId,
            monster.ObjectId,
            monster.SpawnGeneration,
            combatEventId));

    private bool CanApplyEcsMonsterIncomingPreResolution(
        GameSessionContext target,
        MonsterRuntimeUpdate attack,
        ulong combatEventId)
    {
        var currentLifeRevision = _playerLifeRevisions.GetOrAdd(
            target.Session,
            0);
        var lastEventId = GetPlayerVitalsDamageEcsDiagnostics(
            target.Session)?.LastAttackEventId ?? 0;
        return target.Character.CurrentHp > 0 &&
            (attack.TargetObjectId ?? target.ObjectId) == target.ObjectId &&
            (attack.TargetLifeRevision ?? currentLifeRevision) ==
                currentLifeRevision &&
            (attack.TargetVitalsRevision ??
                target.Character.VitalsRevision) ==
                target.Character.VitalsRevision &&
            combatEventId > lastEventId;
    }

    private CombatResolution AdjustMonsterIncomingElementalDamageLocked(
        GameSessionContext target,
        MonsterRuntimeSnapshot monster,
        ulong combatEventId,
        DateTimeOffset authoritativeAt,
        in CombatResolution original,
        out MonsterIncomingElementalAttempt attempt)
    {
        if (!original.Hit)
        {
            attempt = default;
            return original;
        }

        var combatEvent = new DeterministicCombatEventContext(
            combatEventId,
            target.MapId,
            monster.ObjectId,
            target.CharacterId,
            authoritativeAt.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectBasicAttack,
            Committed: false,
            IsPvp: false,
            default);
        var adjustment = UnchangedMonsterIncomingAdjustment(
            original.Damage,
            target.Character.CurrentHp);
        var fence = new ElementalCombatSessionFence(
            target.CharacterId,
            target.MapId,
            target.Ownership);
        if (TryAdjustElementalIncomingHit(
                target.Session,
                fence,
                combatEvent,
                target.Character.ElementalEquipment,
                original.Damage,
                target.Character.CurrentHp,
                target.Character.MaxHp,
                target.Character.MaxMp,
                out var adjusted))
        {
            adjustment = adjusted;
        }
        attempt = new(combatEvent, adjustment);
        if (adjustment.Evaded)
        {
            return original with
            {
                Outcome = CombatHitOutcome.Miss,
                Damage = 0
            };
        }

        return original with
        {
            Damage = checked((uint)Math.Clamp(
                adjustment.AdjustedDamage,
                0,
                uint.MaxValue))
        };
    }

    private MonsterIncomingElementalPostCommit
        CommitMonsterIncomingElementalLocked(
            GameSessionContext target,
            MonsterRuntimeSnapshot monster,
            in MonsterIncomingElementalAttempt attempt,
            uint appliedDirectDamage)
    {
        var character = target.Character;
        var beforeHealth = character.CurrentHp;
        var beforeMana = character.CurrentMp;
        var beforeRevision = character.VitalsRevision;
        if (character.CurrentHp > 0)
        {
            var requestedHealth = AdjustMonsterIncomingElementalHealingLocked(
                target,
                attempt.CombatEvent.AuthoritativeTimeMilliseconds,
                attempt.Adjustment.GuardHealthRecovery);
            character.CurrentHp = checked((int)Math.Min(
                character.MaxHp,
                checked((long)character.CurrentHp + requestedHealth)));
            character.CurrentMp = checked((int)Math.Min(
                character.MaxMp,
                checked((long)character.CurrentMp +
                    attempt.Adjustment.GuardManaRecovery)));
            if (character.CurrentHp != beforeHealth ||
                character.CurrentMp != beforeMana)
            {
                character.MarkVitalsChanged();
            }
        }

        ResonanceDamageIntent? reflection = null;
        if (appliedDirectDamage > 0)
        {
            var committedEvent = attempt.CombatEvent with
            {
                Committed = true
            };
            var fence = new ElementalCombatSessionFence(
                target.CharacterId,
                target.MapId,
                target.Ownership);
            _ = TryPlanCommittedElementalReflection(
                target.Session,
                fence,
                committedEvent,
                character.ElementalEquipment,
                appliedDirectDamage,
                monster.MaximumHealth,
                out reflection);
        }

        return new(
            beforeHealth,
            character.CurrentHp,
            beforeMana,
            character.CurrentMp,
            beforeRevision,
            character.VitalsRevision,
            reflection);
    }

    private long AdjustMonsterIncomingElementalHealingLocked(
        GameSessionContext target,
        long authoritativeTimeMilliseconds,
        long requestedHealth)
    {
        if (requestedHealth <= 0)
        {
            return 0;
        }

        var fence = new ElementalCombatSessionFence(
            target.CharacterId,
            target.MapId,
            target.Ownership);
        return TryGetElementalStatusAdjustment(
            target.Session,
            fence,
            authoritativeTimeMilliseconds,
            movementSpeed: 0,
            physicalDefense: 0,
            magicDefense: 0,
            hitRating: 0,
            healingReceived: requestedHealth,
            out var status)
            ? status.HealingReceived
            : requestedHealth;
    }

    private PveElementalCommitResult
        CommitMonsterIncomingElementalReflection(
            GameSessionContext source,
            MonsterRuntimeSnapshot monster,
            ResonanceDamageIntent? reflection)
    {
        if (reflection is not { } intent ||
            intent.Kind != ResonanceDamageKind.GaiaReflection ||
            intent.Provenance != CombatEventProvenance.Reflection ||
            intent.SourceCharacterId != source.CharacterId ||
            intent.TargetId != monster.ObjectId ||
            intent.SourceEventId == 0 ||
            intent.Damage <= 0)
        {
            return PveElementalCommitResult.Empty;
        }

        var key = new MonsterIncomingAttackCommitKey(
            MonsterIncomingAttackCommitPhase.GaiaReflection,
            source.CharacterId,
            monster.ObjectId,
            monster.SpawnGeneration,
            intent.SourceEventId);
        if (!_monsterIncomingAttackReplay.TryClaim(key))
        {
            return PveElementalCommitResult.Empty;
        }

        var requestedDamage = checked((uint)Math.Clamp(
            intent.Damage,
            0,
            uint.MaxValue));
        if (!TryApplyMonsterDamage(
                source.MapId,
                monster.ObjectId,
                requestedDamage,
                source.CharacterId,
                monster.SpawnGeneration,
                out var damageResult) ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            _monsterIncomingAttackReplay.Release(key);
            return PveElementalCommitResult.Empty;
        }

        return new PveElementalCommitResult(
            [],
            [new PveElementalDamageCommit(
                intent.Kind,
                intent.SourceEventId,
                damageResult)],
            [],
            default);
    }

    private static IncomingResonanceAdjustment
        UnchangedMonsterIncomingAdjustment(
            long damage,
            long currentHealth) =>
        new(
            damage,
            damage,
            PreventedDamage: 0,
            Math.Max(0, currentHealth - Math.Max(0, damage)),
            Evaded: false,
            PoseidonGuardApplied: false,
            ApolloLethalProtectionApplied: false,
            ConsumedBarrier: 0,
            GuardHealthRecovery: 0,
            GuardManaRecovery: 0);
}

internal enum MonsterIncomingAttackCommitPhase : byte
{
    DirectAttack = 1,
    GaiaReflection = 2
}

internal readonly record struct MonsterIncomingAttackCommitKey(
    MonsterIncomingAttackCommitPhase Phase,
    int CharacterId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    ulong CombatEventId)
{
    public bool IsValid =>
        Enum.IsDefined(Phase) &&
        CharacterId > 0 &&
        MonsterObjectId > 0 &&
        MonsterSpawnGeneration > 0 &&
        CombatEventId > 0;
}

internal sealed class MonsterIncomingAttackReplayLedger
{
    private const int Capacity = 4_096;
    private readonly object _gate = new();
    private readonly HashSet<MonsterIncomingAttackCommitKey> _claimed = [];
    private readonly Queue<MonsterIncomingAttackCommitKey> _order = [];

    public bool TryClaim(in MonsterIncomingAttackCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (!_claimed.Add(key))
            {
                return false;
            }

            _order.Enqueue(key);
            while (_order.Count > Capacity)
            {
                _claimed.Remove(_order.Dequeue());
            }

            return true;
        }
    }

    public bool Release(in MonsterIncomingAttackCommitKey key)
    {
        if (!key.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(key));
        }

        lock (_gate)
        {
            if (!_claimed.Remove(key))
            {
                return false;
            }

            var released = key;
            var retained = _order
                .Where(value => value != released)
                .ToArray();
            _order.Clear();
            foreach (var value in retained)
            {
                _order.Enqueue(value);
            }

            return true;
        }
    }
}
