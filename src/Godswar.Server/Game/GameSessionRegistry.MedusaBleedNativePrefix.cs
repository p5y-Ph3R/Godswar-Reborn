using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private const int MedusaNativePrefixMaskWidth = sizeof(ulong) * 8;

#if DEBUG
    private Action? _protocolCheckBeforeMedusaBleedVitalsPersistence = null;
    private int _protocolCheckMedusaBleedSourceRosterDriftPending = 0;
#endif

    /// <summary>
    /// Allocation-free receipt for the exact recipients whose committed
    /// Bleed impact+damage pair was admitted before routine persistence.
    /// The later native suffix may consult this receipt, but may never emit
    /// either prefix packet again.
    /// </summary>
    private readonly record struct MedusaBleedNativePrefixAdmission(
        bool Required,
        bool SelfAdmitted,
        ulong ObserverAdmissionMask)
    {
        public bool IsObserverAdmitted(int index) =>
            index is >= 0 and < MedusaNativePrefixMaskWidth &&
            (ObserverAdmissionMask & (1UL << index)) != 0;
    }

    private static bool IsCommittedBleed(
        in MonsterAttackEcsTransaction transaction) =>
        transaction.MedusaOutcome ==
            MedusaMonsterPlayerHitCommitOutcome.AppliedWithEffect &&
        transaction.MedusaMechanicsResult is
        {
            Effect.Definition.Kind: MedusaEncounterEffectKind.Bleed
        };

    private static bool HasExactCommittedBleedNativePrefixObligation(
        MonsterRuntimeUpdate attack,
        in MonsterAttackEcsTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(attack);
        if (transaction.MedusaOutcome !=
                MedusaMonsterPlayerHitCommitOutcome.AppliedWithEffect ||
            transaction.TargetContext is not { } target ||
            !transaction.Decision.Applied ||
            transaction.Decision.AppliedDamage == 0 ||
            transaction.Decision.Killed ||
            transaction.MedusaMechanicsResult is not
            {
                Outcome: MedusaMechanicHitOutcome.Applied or
                    MedusaMechanicHitOutcome.Refreshed,
                Effect: { } effect,
                PeriodicDamage: null
            } ||
            transaction.MedusaSourceAuthority is not { } source ||
            !source.IsValid ||
            source.Route.WorldInstanceId != target.WorldInstanceId ||
            source.Route.WorldRevision != target.WorldRevision ||
            source.Route.Ownership != target.Ownership ||
            source.Route.LifeRevision !=
                transaction.Decision.BeforeLifeRevision ||
            source.Route.LifeRevision !=
                transaction.Decision.AfterLifeRevision ||
            source.Route.WorldMembershipEpoch !=
                target.WorldMembershipEpoch ||
            source.ObjectId != attack.Monster.ObjectId ||
            source.SpawnGeneration != attack.Monster.SpawnGeneration ||
            source.HealthRevision != attack.Monster.HealthRevision ||
            source.AttachmentRuntimeInstanceId !=
                attack.Monster.RuntimeInstanceId ||
            source.TemplateKey != attack.Monster.Definition.TemplateKey ||
            source.AttackEventId != transaction.Decision.AttackEventId ||
            transaction.Decision.MonsterObjectId != source.ObjectId ||
            effect.Definition.Kind != MedusaEncounterEffectKind.Bleed ||
            !effect.Definition.ClientProjection
                .RequiresCompatibilityDecision ||
            effect.Definition.ClientProjection.NativeReferenceStatusId !=
                18 ||
            effect.Definition.ClientProjection.EmittableStatusId is not null ||
            effect.Definition.ClientProjection
                .MatchedNativeClientSceneId is not null ||
            effect.TargetOwnership != target.Ownership ||
            effect.TargetLifeRevision !=
                transaction.Decision.AfterLifeRevision ||
            effect.TargetWorldMembershipEpoch !=
                target.WorldMembershipEpoch ||
            effect.SourceRosterSpawnId != source.RosterSpawnId ||
            effect.SourceObjectId != source.ObjectId ||
            effect.SourceSpawnGeneration != source.SpawnGeneration)
        {
            return false;
        }

        return ResolveMedusaMonsterImpactSkillId(attack, transaction) ==
            DefaultMonsterImpactSkillId;
    }

    private MedusaBleedNativePrefixAdmission
        AdmitCommittedBleedNativePrefix(
            WorldInstanceRuntime runtime,
            MonsterRuntimeUpdate attack,
            MonsterAttackEcsTransaction transaction,
            IReadOnlyList<MonsterAttackPublicationRecipient> recipients,
            GameSessionContext targetContext,
            in CombatResolution resolution)
    {
        if (!IsCommittedBleed(transaction))
        {
            return default;
        }

        var exactTransaction = transaction;
#if DEBUG
        if (Interlocked.Exchange(
                ref _protocolCheckMedusaBleedSourceRosterDriftPending,
                0) == 1 &&
            transaction.MedusaSourceAuthority is { } source)
        {
            exactTransaction = transaction with
            {
                MedusaSourceAuthority = source with
                {
                    RosterSpawnId = source.RosterSpawnId +
                        "-protocol-check-drift"
                }
            };
        }
#endif
        var exactSafe = false;
        try
        {
            exactSafe = HasExactCommittedBleedNativePrefixObligation(
                attack,
                exactTransaction);
        }
        catch
        {
            // Strict validation faults are unsafe committed-Bleed drift.
        }
        if (!exactSafe)
        {
            // Once owner commit identifies Bleed, invariant drift must never
            // downgrade it to the ordinary post-persistence publisher.
            FailClosedMonsterAttackPrefixTarget(
                targetContext,
                transaction.Decision.AfterLifeRevision);
            return new(
                Required: true,
                SelfAdmitted: false,
                ObserverAdmissionMask: 0);
        }

        var decision = transaction.Decision;
        var monster = attack.Monster;
        var selfAdmitted = false;
        ulong observerMask = 0;
        // Each observer is admitted before self so a self egress failure and
        // immediate exact disconnect cannot stale otherwise eligible peers.
        // Cross-recipient order has no protocol meaning; each recipient still
        // owns one atomic impact-then-damage pair.
        for (var index = 0; index < recipients.Count; index++)
        {
            var captured = recipients[index];
            var observer = captured.Context;

            try
            {
                observer = RebaseMedusaPostCommitContext(
                    transaction.MedusaOutcome,
                    captured.Context,
                    captured.LifeRevision)!;
                if (!observer.WorldReady ||
                    ReferenceEquals(
                        observer.Session,
                        targetContext.Session) ||
                    !runtime.Map.IsMonsterVisibleTo(
                        observer.Session,
                        monster.ObjectId))
                {
                    continue;
                }
                if (index >= MedusaNativePrefixMaskWidth)
                {
                    FailClosedMonsterAttackPrefixRecipient(
                        targetContext,
                        decision.AfterLifeRevision,
                        observer,
                        captured.LifeRevision);
                    continue;
                }

                var worldPrefix = BuildMonsterAttackNativePrefix(
                    attack,
                    resolution,
                    targetContext.ObjectId,
                    "World");
                if (WasMonsterAttackBatchOwned(
                    TrySendMonsterAttackPacketBatchExactOutcome(
                        runtime,
                        observer,
                        captured.LifeRevision,
                        targetContext,
                        decision.AfterLifeRevision,
                        [worldPrefix.Impact, worldPrefix.Damage],
                        CancellationToken.None,
                        "MonsterBleedPrefixWorld")))
                {
                    observerMask |= 1UL << index;
                }
            }
            catch
            {
                FailClosedMonsterAttackPrefixRecipient(
                    targetContext,
                    decision.AfterLifeRevision,
                    observer,
                    captured.LifeRevision);
            }
        }

        try
        {
            var selfPrefix = BuildMonsterAttackNativePrefix(
                attack,
                resolution,
                LocalPlayerObjectId,
                "Self");
            selfAdmitted = WasMonsterAttackBatchOwned(
                TrySendMonsterAttackPacketBatchExactOutcome(
                    runtime,
                    targetContext,
                    decision.AfterLifeRevision,
                    targetContext,
                    decision.AfterLifeRevision,
                    [selfPrefix.Impact, selfPrefix.Damage],
                    CancellationToken.None,
                    "MonsterBleedPrefixSelf"));
        }
        catch
        {
            FailClosedMonsterAttackPrefixTarget(
                targetContext,
                decision.AfterLifeRevision);
        }

        return new(
            Required: true,
            SelfAdmitted: selfAdmitted,
            ObserverAdmissionMask: observerMask);
    }

    private (byte[] Impact, byte[] Damage)
        BuildMonsterAttackNativePrefix(
            MonsterRuntimeUpdate attack,
            in CombatResolution resolution,
            uint targetObjectId,
            string stagePrefix)
    {
        var monster = attack.Monster;
        InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
            stagePrefix + "Impact");
        var impact = PacketBuilder.SkillCastImpact(
            monster.ObjectId,
            targetObjectId,
            DefaultMonsterImpactSkillId,
            attack.TargetX,
            attack.TargetZ);

        InvokeProtocolCheckBeforeMedusaNativePrefixPacket(
            stagePrefix + "Damage");
        var damage = PacketBuilder.PhysicalDamage(
            monster.ObjectId,
            monster.X,
            monster.Y,
            monster.Z,
            targetObjectId,
            resolution.CapturedDamageValue,
            result: 0,
            damageType: (byte)resolution.Outcome);
        return (impact, damage);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckBeforeMedusaBleedVitalsPersistence(
        bool committedBleed)
    {
#if DEBUG
        if (committedBleed)
        {
            _protocolCheckBeforeMedusaBleedVitalsPersistence?.Invoke();
        }
#endif
    }
}
