using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<LegacyHostileSkillPublication>
        PublishLegacyHostileMonsterSkillHitAsync(
            GamePacket packet,
            SkillCastRequest cast,
            MonsterDamageResult damageResult,
            uint reportedDamage,
            int manaCost,
            int currentMana,
            bool publishCastVisual,
            CancellationToken cancellationToken)
    {
        var character = _character!;
        var targetX = damageResult.Monster.X;
        var targetZ = damageResult.Monster.Z;
        var selfVisual = PacketBuilder.SkillCastVisual(
            packet.Buffer,
            LocalPlayerObjectId);
        var selfDamage = PacketBuilder.SkillDamage(
            LocalPlayerObjectId,
            cast.TargetObjectId,
            resultFlags: 1,
            reportedDamage,
            cast.SkillId,
            targetX,
            targetZ);
        var selfImpact = PacketBuilder.SkillCastImpact(
            LocalPlayerObjectId,
            cast.TargetObjectId,
            cast.SkillId,
            targetX,
            targetZ);

        var casterNotified = true;
        try
        {
            if (publishCastVisual)
            {
                await _registry.DeliverMonsterPacketToViewerAsync(
                    _session,
                    character.CurrentMap,
                    cast.TargetObjectId,
                    selfVisual,
                    damageResult.Monster.SpawnGeneration,
                    cancellationToken,
                    "SkillCastSelf");
            }

            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                selfDamage,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "SkillDamageSelf");
            await _registry.DeliverMonsterPacketToViewerAsync(
                _session,
                character.CurrentMap,
                cast.TargetObjectId,
                selfImpact,
                damageResult.Monster.SpawnGeneration,
                cancellationToken,
                "SkillCastImpactSelf");
            if (manaCost > 0)
            {
                lock (character.VitalsSync)
                {
                    currentMana = character.CurrentMp;
                }

                await _session.SendAsync(
                    PacketBuilder.PlayerManaUpdate(
                        LocalPlayerObjectId,
                        currentMana),
                    cancellationToken,
                    "SkillManaSelf");
            }
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[skill] caster notification failed character={character.Name} target={cast.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = CurrentPlayerObjectId;
        var visualRecipients = publishCastVisual
            ? await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastVisual(
                    packet.Buffer,
                    worldObjectId),
                cancellationToken,
                _session,
                "SkillCastWorld",
                expectedSpawnGeneration:
                    damageResult.Monster.SpawnGeneration)
            : 0;
        var damageRecipients =
            await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillDamage(
                    worldObjectId,
                    cast.TargetObjectId,
                    resultFlags: 1,
                    reportedDamage,
                    cast.SkillId,
                    targetX,
                    targetZ),
                cancellationToken,
                _session,
                "SkillDamageWorld",
                healthMutation: damageResult.HealthMutation);
        var impactRecipients =
            await _registry.BroadcastToMonsterViewersAsync(
                character.CurrentMap,
                cast.TargetObjectId,
                PacketBuilder.SkillCastImpact(
                    worldObjectId,
                    cast.TargetObjectId,
                    cast.SkillId,
                    targetX,
                    targetZ),
                cancellationToken,
                _session,
                "SkillCastImpactWorld",
                expectedSpawnGeneration:
                    damageResult.Monster.SpawnGeneration);
        return new(
            casterNotified,
            Math.Max(
                visualRecipients,
                Math.Max(damageRecipients, impactRecipients)),
            currentMana);
    }

    private readonly record struct LegacyHostileSkillPublication(
        bool CasterNotified,
        int ViewerCount,
        int CurrentMana);
}
