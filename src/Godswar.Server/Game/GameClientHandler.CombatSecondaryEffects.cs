using Godswar.Server.Packets;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct PveCommittedMonsterDamage(
    ulong CombatEventId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    uint AppliedDamage);

internal readonly record struct PveLifeAbsorptionCommit(
    int ClaimedHitCount,
    uint RequestedHealing,
    uint AdjustedRequestedHealing,
    int AppliedHealing,
    int BeforeHealth,
    int AfterHealth,
    long BeforeVitalsRevision,
    long AfterVitalsRevision)
{
    public bool Applied => AppliedHealing > 0;
}

internal sealed class PveLifeAbsorptionCommitter
{
    private readonly CombatSecondaryEffectCommitLedger
        _ledger;

    public PveLifeAbsorptionCommitter(
        int ledgerCapacity =
            CombatSecondaryEffectCommitLedger.DefaultCapacity)
    {
        _ledger = new CombatSecondaryEffectCommitLedger(
            ledgerCapacity);
    }

    public PveLifeAbsorptionCommit Commit(
        GameCharacter character,
        IReadOnlyList<PveCommittedMonsterDamage> committedHits,
        int healingReceivedBasisPoints =
            ElementalBasisPointMath.Denominator)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(committedHits);
        if (committedHits.Count == 0)
        {
            return default;
        }

        var attacker = CombatCharacterStatsAdapter.FromCharacter(character);
        var boundedHealingReceived =
            ElementalBasisPointMath.ClampBasisPoints(
                healingReceivedBasisPoints,
                ElementalBasisPointMath.Denominator);
        var claimedHitCount = 0;
        ulong requestedHealing = 0;
        ulong adjustedRequestedHealing = 0;
        foreach (var hit in committedHits)
        {
            if (hit.AppliedDamage == 0)
            {
                continue;
            }

            var key = new CombatSecondaryEffectCommitKey(
                CombatSecondaryEffectCommitKind.LifeAbsorption,
                character.Id,
                hit.MonsterObjectId,
                hit.MonsterSpawnGeneration,
                hit.CombatEventId);
            if (!_ledger.TryClaim(key))
            {
                continue;
            }

            claimedHitCount++;
            var hitHealing = CombatSecondaryEffectPolicy.Resolve(
                    hit.AppliedDamage,
                    attacker,
                    default)
                .LifeAbsorptionHealing;
            requestedHealing = Math.Min(
                uint.MaxValue,
                requestedHealing + hitHealing);
            adjustedRequestedHealing = Math.Min(
                uint.MaxValue,
                adjustedRequestedHealing + checked((uint)
                    ElementalBasisPointMath.Portion(
                        hitHealing,
                        boundedHealingReceived)));
        }

        lock (character.VitalsSync)
        {
            var beforeHealth = character.CurrentHp;
            var beforeRevision = character.VitalsRevision;
            if (claimedHitCount == 0 ||
                requestedHealing == 0 ||
                beforeHealth <= 0)
            {
                return new PveLifeAbsorptionCommit(
                    claimedHitCount,
                    (uint)requestedHealing,
                    (uint)adjustedRequestedHealing,
                    0,
                    beforeHealth,
                    beforeHealth,
                    beforeRevision,
                    beforeRevision);
            }

            var appliedHealing =
                CombatSecondaryEffectPolicy
                    .ClampLifeAbsorptionToMissingHealth(
                        (uint)adjustedRequestedHealing,
                        beforeHealth,
                        character.MaxHp);
            if (appliedHealing > 0)
            {
                character.CurrentHp = checked(
                    beforeHealth + appliedHealing);
                character.MarkVitalsChanged();
            }

            return new PveLifeAbsorptionCommit(
                claimedHitCount,
                (uint)requestedHealing,
                (uint)adjustedRequestedHealing,
                appliedHealing,
                beforeHealth,
                character.CurrentHp,
                beforeRevision,
                character.VitalsRevision);
        }
    }
}

internal sealed partial class GameClientHandler
{
    private readonly PveLifeAbsorptionCommitter
        _pveLifeAbsorptionCommitter = new();

    private PveLifeAbsorptionCommit CommitPveLifeAbsorption(
        GameCharacter character,
        IReadOnlyList<PveCommittedMonsterDamage> committedHits)
    {
        var healingReceivedBasisPoints = checked((int)Math.Clamp(
            _registry.AdjustElementalHealingReceived(
                _session,
                character,
                DateTimeOffset.UtcNow,
                ElementalBasisPointMath.Denominator),
            0,
            ElementalBasisPointMath.Denominator));
        return _pveLifeAbsorptionCommitter.Commit(
            character,
            committedHits,
            healingReceivedBasisPoints);
    }

    private static PveCommittedMonsterDamage
        CreatePveCommittedMonsterDamage(
            in CombatResolution resolution,
            MonsterDamageResult damageResult) =>
        new(
            resolution.EventId,
            damageResult.ObjectId,
            damageResult.Monster.SpawnGeneration,
            damageResult.BeforeHealth - damageResult.AfterHealth);

    private static PveCommittedMonsterDamage[]
        CreatePveCommittedMonsterDamage(
            PlayerCombatEcsDecision decision)
    {
        if (decision.Hits.IsEmpty)
        {
            return [];
        }

        var committedHits = new PveCommittedMonsterDamage[
            decision.Hits.Length];
        for (var index = 0; index < decision.Hits.Length; index++)
        {
            var hit = decision.Hits[index];
            PlayerCombatEcsResolvedTarget? matched = null;
            foreach (var candidate in decision.Resolutions)
            {
                if (candidate.TargetObjectId != hit.Result.ObjectId ||
                    candidate.SpawnGeneration !=
                    hit.Result.Monster.SpawnGeneration)
                {
                    continue;
                }

                if (matched is not null)
                {
                    throw new InvalidOperationException(
                        "An ECS monster hit matched multiple combat resolutions.");
                }

                matched = candidate;
            }

            if (matched is not { Resolution.Hit: true } resolved)
            {
                throw new InvalidOperationException(
                    "An ECS monster health mutation has no hit resolution.");
            }

            committedHits[index] = CreatePveCommittedMonsterDamage(
                resolved.Resolution,
                hit.Result);
        }

        return committedHits;
    }

    private async Task PublishPveLifeAbsorptionAsync(
        GameCharacter character,
        PveLifeAbsorptionCommit commit,
        CancellationToken cancellationToken,
        bool persistVitals = true)
    {
        if (!commit.Applied)
        {
            return;
        }

        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);
        if (persistVitals)
        {
            try
            {
                if (!await PersistVitalsCheckpointAsync(
                        character,
                        force: false,
                        cancellationToken))
                {
                    Console.WriteLine(
                        "[combat] life-absorption vitals checkpoint deferred " +
                        $"character={character.Name} revision=" +
                        commit.AfterVitalsRevision);
                }
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[combat] life-absorption vitals persistence deferred " +
                    $"character={character.Name}: {ex.Message}");
            }
        }

        int currentHp;
        int currentMp;
        lock (character.VitalsSync)
        {
            currentHp = character.CurrentHp;
            currentMp = character.CurrentMp;
        }

        try
        {
            await _session.SendAsync(
                PacketBuilder.PlayerVitalsUpdate(
                    LocalPlayerObjectId,
                    currentHp,
                    currentMp),
                cancellationToken,
                "PveLifeAbsorptionSelf");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            _registry.Remove(_session);
        }

        await _registry.BroadcastToMapAsync(
            character.CurrentMap,
            PacketBuilder.PlayerVitalsUpdate(
                CurrentPlayerObjectId,
                currentHp,
                currentMp),
            cancellationToken,
            _session,
            "PveLifeAbsorptionWorld");
    }

    private async Task<PreparedPveMonsterKillReward?>
        PreparePveDerivedKillRewardAsync(
            MonsterDamageResult damageResult)
    {
        var pending = await PrepareMonsterKillRewardAsync(damageResult);
        return pending is null
            ? null
            : new PreparedPveMonsterKillReward(
                cancellationToken => PublishMonsterKillRewardAsync(
                    pending,
                    cancellationToken));
    }
}
