using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleHostileMonsterAreaSkillCastEcsAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition combat,
        float areaCenterX,
        float areaCenterZ,
        bool publishCastVisual,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "ecs_area_skill_damage"))
        {
            return;
        }

        var isGroundTargeted =
            SkillCombatResolver.IsHostileMonsterGroundAreaSkill(combat);
        var manaCost = Math.Max(0, combat.Mp);
        var decision = _registry.ResolvePlayerCombatEcs(
            _session,
            character,
            LocalPlayerObjectId,
            _nextBasicAttackAt,
            PlayerCombatEcsRequest.HostileSkill(
                PlayerCombatIntentKind.AreaSkill,
                DateTimeOffset.UtcNow,
                uint.MaxValue,
                combat,
                hasTargetPosition: isGroundTargeted,
                areaCenterX: areaCenterX,
                areaCenterZ: areaCenterZ));
        if (!decision.IntentAccepted)
        {
            if (decision.RejectionReason ==
                PlayerCombatRejectionReason.InsufficientMana)
            {
                Console.WriteLine(
                    $"[skill] rejected insufficient MP character={character.Name} skill={cast.SkillId} mp={decision.CurrentMana} cost={manaCost}");
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(
                        LocalPlayerObjectId,
                        decision.CurrentMana),
                    cancellationToken,
                    "AreaSkillManaRejected");
            }
            else
            {
                Console.WriteLine(
                    $"[skill] rejected area combat character={character.Name} skill={cast.SkillId} reason={decision.RejectionReason}");
            }

            return;
        }

        var hits = decision.Hits;
        _registry.UpdateCharacter(
            _session,
            character,
            advanceWorldRevision: false);
        var pendingRewards = new List<PendingMonsterKillReward>();
        foreach (var hit in hits)
        {
            if (!hit.Result.Killed)
            {
                continue;
            }

            var pendingReward =
                await PrepareMonsterKillRewardAsync(hit.Result);
            if (pendingReward is not null)
            {
                pendingRewards.Add(pendingReward);
            }
        }

        var selfVisual = isGroundTargeted
            ? PacketBuilder.SkillCastVisual(
                packet.Buffer,
                LocalPlayerObjectId)
            : PacketBuilder.SelfTargetSkillCastVisual(
                packet.Buffer,
                LocalPlayerObjectId);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            uint.MaxValue,
            cast.SkillId,
            areaCenterX,
            areaCenterZ);
        var selfCluster = PacketBuilder.SkillClusterDamage(
            LocalPlayerObjectId,
            cast.SkillId,
            hits.Select(static hit =>
                    new SkillClusterDamageEntry(
                        hit.Result.ObjectId,
                        hit.ReportedDamage))
                .ToArray());

        var casterNotified = true;
        try
        {
            if (publishCastVisual)
            {
                await _session.SendAsync(
                    selfVisual,
                    cancellationToken,
                    "AreaSkillCastSelf");
            }
            await _session.SendAsync(
                selfImpact,
                cancellationToken,
                "AreaSkillImpactSelf");
            if (hits.Length == 0)
            {
                await _session.SendAsync(
                    selfCluster,
                    cancellationToken,
                    "AreaSkillDamageSelf");
            }
            else
            {
                await _registry.DeliverMonsterAreaDamageToViewerAsync(
                    _session,
                    character.CurrentMap,
                    LocalPlayerObjectId,
                    cast.SkillId,
                    hits.Select(static hit =>
                            new MonsterAreaDamageBroadcastHit(
                                hit.Result.HealthMutation!.Value,
                                hit.ReportedDamage))
                        .ToArray(),
                    cancellationToken,
                    "AreaSkillSelf");
            }

            if (manaCost > 0)
            {
                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(
                        LocalPlayerObjectId,
                        decision.CurrentMana),
                    cancellationToken,
                    "AreaSkillManaSelf");
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] area caster notification failed character={character.Name} skill={cast.SkillId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);
        var worldVisual = isGroundTargeted
            ? PacketBuilder.SkillCastVisual(
                packet.Buffer,
                worldObjectId)
            : PacketBuilder.SelfTargetSkillCastVisual(
                packet.Buffer,
                worldObjectId);
        var areaRecipients =
            await _registry.BroadcastMonsterAreaDamageToViewersAsync(
                character.CurrentMap,
                worldVisual,
                PacketBuilder.SkillCastImpact(
                    worldObjectId,
                    uint.MaxValue,
                    cast.SkillId,
                    areaCenterX,
                    areaCenterZ),
                worldObjectId,
                cast.SkillId,
                hits.Select(static hit =>
                        new MonsterAreaDamageBroadcastHit(
                            hit.Result.HealthMutation!.Value,
                            hit.ReportedDamage))
                    .ToArray(),
                cancellationToken,
                _session,
                "AreaSkill",
                publishCastVisual);

        foreach (var pendingReward in pendingRewards)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        await PersistSkillVitalsAsync(
            character,
            areaSkill: true,
            cancellationToken);

        int currentMana;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
        }

        var appliedDamage = hits.Aggregate(
            0UL,
            static (total, hit) =>
                total +
                hit.Result.BeforeHealth -
                hit.Result.AfterHealth);
        var reportedDamage = hits.Length == 0
            ? 0
            : hits[0].ReportedDamage;
        Console.WriteLine(
            $"[skill] area damage character={character.Name} skill={cast.SkillId} center={areaCenterX:F2},{areaCenterZ:F2} radius={combat.Range:F2} candidates={decision.SelectedTargetCount} hits={hits.Length} resolved-each={reportedDamage} applied-total={appliedDamage} mp={currentMana}/{character.MaxMp} caster-notified={casterNotified} viewers={areaRecipients}");
    }
}
