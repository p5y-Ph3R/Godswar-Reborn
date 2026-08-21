using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleSkillCastAsync(
        GamePacket packet,
        CancellationToken cancellationToken,
        bool intonationCompleted = false,
        uint? expectedTargetSpawnGeneration = null,
        SkillCombatDefinition? intonedCombatSnapshot = null)
    {
        if (_character is null)
        {
            Console.WriteLine("[skill] ignored cast before character enter");
            return;
        }

        if (!intonationCompleted &&
            _character.CurrentHp <= 0)
        {
            Console.WriteLine($"[skill] ignored cast from dead character={_character.Name}");
            return;
        }

        if (!SkillCastRequest.TryParse(packet.Buffer, out var cast))
        {
            Console.WriteLine($"[skill] ignored cast payload too short len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        var control = ResolvePlayerSkillCastControl(
            DateTimeOffset.UtcNow);
        // The coordinator validates control statuses before atomically
        // claiming completion. A status applied after that claim belongs to
        // the next action and must not discard this already-completed cast.
        if (!intonationCompleted &&
            control != PlayerSkillCastControl.None)
        {
            var hadPendingCast = HasPendingSkillCast;
            await InterruptPendingSkillCastAsync(
                PlayerSkillCastControlCatalog.ToInterruptionReason(
                    control),
                cancellationToken);
            if (!hadPendingCast)
            {
                await SendBlockedSkillCastNoticeAsync(
                    control,
                    cancellationToken);
            }
            return;
        }

        if (!intonationCompleted && HasPendingSkillCast)
        {
            await InterruptPendingSkillCastAsync(
                SkillCastInterruptionReason.Replaced,
                cancellationToken);
        }

        var castX = float.IsFinite(cast.CasterX) ? cast.CasterX : _character.PositionX;
        var castZ = float.IsFinite(cast.CasterZ) ? cast.CasterZ : _character.PositionZ;
        var learned = await IsSkillLearnedAsync(cast.SkillId, cancellationToken);

        Console.WriteLine(
            $"[skill] cast character={_character.Name} skill={cast.SkillId} learned={learned} caster={cast.CasterObjectId} target={cast.TargetObjectId} x={castX:F2} z={castZ:F2}");
        if (!learned)
        {
            Console.WriteLine(
                $"[skill] rejected unlearned skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        if (cast.SkillId == MountCatalog.RideSkillId)
        {
            await HandleRideSkillCastAsync(packet, cast, cancellationToken);
            return;
        }

        if (BackhaulSkillCatalog.TryGet(
                cast.SkillId,
                out var backhaul))
        {
            await HandleBackhaulSkillCastAsync(
                packet,
                cast,
                backhaul,
                cancellationToken);
            return;
        }

        if (cast.SkillId <= int.MaxValue &&
            SkillStatusEffectCatalog.TryGet((int)cast.SkillId, out var statusEffect))
        {
            await HandleBeneficialStatusSkillCastAsync(
                packet,
                cast,
                statusEffect,
                cancellationToken);
            return;
        }

        if (cast.SkillId > int.MaxValue ||
            !_gameplayCatalogs.SkillCombat.TryGet(
                (int)cast.SkillId,
                out var authoredCombat))
        {
            Console.WriteLine(
                $"[skill] rejected unsupported combat skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        SkillCombatDefinition combat;
        if (intonedCombatSnapshot is { } pinnedCombat)
        {
            if (!intonationCompleted ||
                pinnedCombat.SkillId != authoredCombat.SkillId)
            {
                Console.WriteLine(
                    $"[skill] rejected invalid intonation snapshot character={_character.Name} skill={cast.SkillId}");
                return;
            }

            combat = pinnedCombat;
        }
        else
        {
            var zodiacOffense = ZodiacOffensiveSkillProjection.Resolve(
                _character,
                authoredCombat);
            combat = zodiacOffense.Skill;
            if (zodiacOffense.Status ==
                ZodiacOffensiveSkillProjectionStatus.InvalidState)
            {
                Console.WriteLine(
                    $"[skill] ignored invalid Zodiac offense character={_character.Name} skill={cast.SkillId}");
            }
        }

        if (PriestHealingSkillCatalog.TryResolve(
                combat,
                out var healing))
        {
            if (!intonationCompleted &&
                combat.CastTime > TimeSpan.Zero)
            {
                await BeginIntonedPriestHealingSkillCastAsync(
                    packet,
                    cast,
                    combat,
                    healing,
                    cancellationToken);
                return;
            }

            await HandlePriestHealingSkillCastAsync(
                packet,
                cast,
                combat,
                healing,
                publishCastVisual: !intonationCompleted,
                cancellationToken);
            return;
        }

        if (TrainingDummyHostileStatusSkillCatalog.TryGet(
                combat.SkillId,
                out var hostileStatus) &&
            hostileStatus.Trigger ==
                HostileStatusApplicationTrigger.CommittedCast)
        {
            if (!intonationCompleted &&
                combat.CastTime > TimeSpan.Zero &&
                await TryBeginIntonedTrainingDummyHostileStatusCastAsync(
                    packet,
                    cast,
                    combat,
                    hostileStatus,
                    cancellationToken))
            {
                return;
            }
            if ((intonationCompleted ||
                 combat.CastTime == TimeSpan.Zero) &&
                await TryHandleTrainingDummyHostileStatusCastAsync(
                    packet,
                    cast,
                    combat,
                    hostileStatus,
                    publishCastVisual: !intonationCompleted,
                    cancellationToken))
            {
                return;
            }
        }

        if (!SkillCombatResolver.IsHostileMonsterSkill(combat))
        {
            Console.WriteLine(
                $"[skill] rejected unsupported combat skill character={_character.Name} skill={cast.SkillId}");
            return;
        }

        var selectedTargetIsOtherPlayer =
            _registry.TryGetCurrentWorldSessionByObjectId(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                out var selectedPlayer) &&
            !ReferenceEquals(selectedPlayer.Character, _character);
        if (selectedTargetIsOtherPlayer &&
            _registry.IsTrainingDummy(selectedPlayer.Character) &&
            TrainingDummyDamageSkillPolicy.IsSupportedScalar(
                _gameplayCatalogs,
                authoredCombat,
                _character.Profession))
        {
            await HandleTrainingDummyDamageScalarAsync(
                packet,
                cast,
                authoredCombat,
                cancellationToken);
            return;
        }
        if (SkillCombatResolver.MustRejectHostilePlayerTarget(
                selectedTargetIsOtherPlayer))
        {
            Console.WriteLine(
                $"[skill] rejected uncaptured hostile player target character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        if (TrainingDummyDamageSkillPolicy.IsSupportedArea(
                _gameplayCatalogs,
                authoredCombat,
                _character.Profession) &&
            await TryHandleTrainingDummyDamageAreaAsync(
                packet,
                cast,
                authoredCombat,
                cancellationToken))
        {
            return;
        }

        if (!intonationCompleted &&
            combat.CastTime > TimeSpan.Zero)
        {
            await BeginIntonedCombatSkillCastAsync(
                packet,
                cast,
                combat,
                cancellationToken);
            return;
        }

        if (SkillCombatResolver.IsHostileMonsterAreaSkill(combat))
        {
            await HandleHostileMonsterAreaSkillCastAsync(
                packet,
                cast,
                combat,
                publishCastVisual: !intonationCompleted,
                cancellationToken);
            return;
        }

        var isMonsterStunSkill =
            cast.SkillId <= int.MaxValue &&
            MonsterStunSkillCatalog.TryGet(
                (int)cast.SkillId,
                out _);
        if (_registry.PlayerRuntimeMode == PlayerRuntimeMode.Ecs &&
            !isMonsterStunSkill)
        {
            await HandleHostileMonsterSingleSkillCastEcsAsync(
                packet,
                cast,
                combat,
                publishCastVisual: !intonationCompleted,
                expectedTargetSpawnGeneration,
                cancellationToken);
            return;
        }

        if (!_registry.TryGetMonsterSnapshot(
                _session,
                _character.CurrentMap,
                cast.TargetObjectId,
                out var target) ||
            expectedTargetSpawnGeneration is { } expectedGeneration &&
            target.SpawnGeneration != expectedGeneration ||
            !_registry.IsMonsterVisibleTo(
                _session,
                cast.TargetObjectId,
                target.SpawnGeneration) ||
            !target.IsSpawned ||
            !target.IsAlive)
        {
            Console.WriteLine(
                $"[skill] rejected unavailable monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        if (!SkillCombatResolver.IsWithinRange(
                _character.PositionX,
                _character.PositionZ,
                target.X,
                target.Z,
                combat))
        {
            Console.WriteLine(
                $"[skill] rejected out-of-range monster character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} player={_character.PositionX:F2},{_character.PositionZ:F2} monster={target.X:F2},{target.Z:F2} range={combat.Distance:F2}");
            return;
        }

        if (cast.SkillId <= int.MaxValue &&
            MonsterStunSkillCatalog.TryGet((int)cast.SkillId, out var stun))
        {
            await HandleHostileMonsterStunSkillCastAsync(
                packet,
                cast,
                combat,
                stun,
                expectedTargetSpawnGeneration ??
                    target.SpawnGeneration,
                cancellationToken);
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "single_skill_damage"))
        {
            return;
        }

        var manaCost = Math.Max(0, combat.Mp);
        var observedAt = DateTimeOffset.UtcNow;
        using var elementalAuthority =
            CapturePveElementalCommitAuthority(_character);
        if (elementalAuthority is null)
        {
            Console.WriteLine(
                $"[skill] rejected stale elemental authority character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        var manaReserved = TryReserveLegacyHostileSkill(
            _character,
            combat,
            observedAt,
            out var currentMana,
            out var cooldownLease,
            out var cooldownRejected);

        if (!manaReserved)
        {
            if (cooldownRejected)
            {
                return;
            }

            Console.WriteLine(
                $"[skill] rejected insufficient MP character={_character.Name} skill={cast.SkillId} mp={currentMana} cost={manaCost}");
            await _session.SendAsync(
                PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                cancellationToken,
                "SkillManaRejected");
            return;
        }

        var admittedCombatRevision =
            checked((ulong)NextAdmittedLegacyCombatRevision());
        var runtimeCombatModifiers =
            _registry.GetRuntimeStatusAggregate(_session, observedAt);
        var resolution = ResolveHostileMonsterSkillDamage(
            _character,
            combat,
            target,
            admittedCombatRevision,
            targetOrder: 0,
            observedAt,
            runtimeCombatModifiers);
        if (!resolution.Hit)
        {
            await PublishUnreportedHostileMonsterSkillMissAsync(
                packet,
                cast,
                combat,
                target.SpawnGeneration,
                publishCastVisual: !intonationCompleted,
                currentMana,
                resolution,
                cancellationToken);
            return;
        }

        var requestedDamage = resolution.Damage;
        if (requestedDamage == 0 ||
            !RevalidateCurrentWorldEffectOwnership(
                "single_skill_damage") ||
            !_registry.TryApplyMonsterDamage(
                _character.CurrentMap,
                cast.TargetObjectId,
                requestedDamage,
                _character.Id,
                expectedTargetSpawnGeneration ??
                    target.SpawnGeneration,
                out var damageResult) ||
            damageResult.BeforeHealth == damageResult.AfterHealth)
        {
            ReleaseHostileSkillCooldown(cooldownLease);
            if (manaCost > 0)
            {
                lock (_character.VitalsSync)
                {
                    _character.CurrentMp = Math.Min(
                        Math.Max(0, _character.MaxMp),
                        (int)Math.Min(int.MaxValue, (long)_character.CurrentMp + manaCost));
                    _character.MarkVitalsChanged();
                    currentMana = _character.CurrentMp;
                }

                try
                {
                    await PersistVitalsCheckpointAsync(
                        _character,
                        force: false,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine(
                        $"[skill] refunded vitals persistence deferred character={_character.Name}: {ex.Message}");
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(LocalPlayerObjectId, currentMana),
                    cancellationToken,
                    "SkillManaRefund");
            }

            Console.WriteLine(
                $"[skill] rejected stale monster target character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId}");
            return;
        }

        var lifeAbsorption = CommitPveLifeAbsorption(
            _character,
            [CreatePveCommittedMonsterDamage(
                resolution,
                damageResult)]);
        var elementalCommit = CommitPveElementalHit(
            elementalAuthority,
            CombatEventProvenance.DirectSkill,
            resolution,
            damageResult);
        var pendingReward = damageResult.Killed
            ? await PrepareMonsterKillRewardAsync(damageResult)
            : null;
        var elementalRewards =
            await PreparePveElementalKillRewardsAsync(
                elementalAuthority,
                elementalCommit);
        _registry.UpdateCharacter(_session, _character, advanceWorldRevision: false);

        var reportedDamage = resolution.CapturedDamageValue;
        var publication =
            await PublishLegacyHostileMonsterSkillHitAsync(
                packet,
                cast,
                damageResult,
                reportedDamage,
                manaCost,
                currentMana,
                publishCastVisual: !intonationCompleted,
                cancellationToken);
        currentMana = publication.CurrentMana;

        await PublishPveLifeAbsorptionAsync(
            _character,
            lifeAbsorption,
            cancellationToken,
            persistVitals: false);

        await PublishPveElementalCommitAsync(
            elementalAuthority,
            elementalCommit,
            elementalRewards,
            cancellationToken);

        if (_account is not null)
        {
            try
            {
                await PersistVitalsCheckpointAsync(
                    _character,
                    force: false,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Database availability must not suppress an already-authoritative
                // shared hit. The in-memory session remains correct and can retry.
                Console.WriteLine(
                    $"[skill] vitals persistence deferred character={_character.Name}: {ex.Message}");
            }
        }

        if (pendingReward is not null)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        lock (_character.VitalsSync)
        {
            currentMana = _character.CurrentMp;
        }

        Console.WriteLine(
            $"[skill] damage character={_character.Name} skill={cast.SkillId} target={cast.TargetObjectId} event={resolution.EventId} outcome={resolution.Outcome} resolved={reportedDamage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} mp={currentMana}/{_character.MaxMp} caster-notified={publication.CasterNotified} viewers={publication.ViewerCount}");
    }

}
