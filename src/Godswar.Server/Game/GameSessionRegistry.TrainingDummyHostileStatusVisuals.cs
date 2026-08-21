using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Task<int> PublishTrainingDummyHostileCastVisualAsync(
        GameSessionContext attacker,
        GameSessionContext? target,
        byte[] clientSkillCastPacket,
        HostileStatusEffectDefinition definition,
        CancellationToken cancellationToken) =>
        PublishTrainingDummyHostileCastPacketAsync(
            attacker,
            target,
            clientSkillCastPacket,
            definition,
            impact: false,
            cancellationToken);

    internal Task<int> PublishTrainingDummyHostileCastImpactAsync(
        GameSessionContext attacker,
        GameSessionContext? target,
        byte[] clientSkillCastPacket,
        HostileStatusEffectDefinition definition,
        CancellationToken cancellationToken) =>
        PublishTrainingDummyHostileCastPacketAsync(
            attacker,
            target,
            clientSkillCastPacket,
            definition,
            impact: true,
            cancellationToken);

    private async Task<int> PublishTrainingDummyHostileCastPacketAsync(
        GameSessionContext attacker,
        GameSessionContext? target,
        byte[] clientSkillCastPacket,
        HostileStatusEffectDefinition definition,
        bool impact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(clientSkillCastPacket);
        if (!WorldInstances.TryFind(
                attacker.WorldInstanceId,
                out WorldInstanceRuntime? runtime))
        {
            return 0;
        }

        var sent = 0;
        foreach (var recipient in GetWorldInstanceSessions(
                     attacker.WorldInstanceId))
        {
            var attackerId = ReferenceEquals(
                    recipient.Session,
                    attacker.Session)
                ? LocalPlayerObjectId
                : attacker.ObjectId;
            var targetId = definition.TargetMode ==
                    HostileStatusTargetMode.SelfCenteredArea
                ? attackerId
                : ReferenceEquals(recipient.Session, target?.Session)
                    ? LocalPlayerObjectId
                    : target?.ObjectId ?? 0;
            if (targetId == 0)
            {
                continue;
            }

            var packet = impact
                ? BuildTrainingDummyHostileImpact(
                    attacker,
                    target,
                    attackerId,
                    targetId,
                    definition)
                : PacketBuilder.SkillCastVisual(
                    clientSkillCastPacket,
                    attackerId,
                    targetId,
                    checked((uint)definition.SkillId));
            try
            {
                if (await TrySendWorldInstancePacketAsync(
                        runtime,
                        recipient,
                        packet,
                        cancellationToken,
                        impact
                            ? "TrainingDummyHostileStatusImpact"
                            : "TrainingDummyHostileStatusVisual"))
                {
                    sent++;
                }
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException)
            {
                Remove(recipient.Session);
            }
        }

        return sent;
    }

    private static byte[] BuildTrainingDummyHostileImpact(
        GameSessionContext attacker,
        GameSessionContext? target,
        uint attackerId,
        uint targetId,
        in HostileStatusEffectDefinition definition)
    {
        var selfArea = definition.TargetMode ==
            HostileStatusTargetMode.SelfCenteredArea;
        return PacketBuilder.SkillCastImpact(
            attackerId,
            selfArea ? uint.MaxValue : targetId,
            checked((uint)definition.SkillId),
            selfArea
                ? attacker.Character.PositionX
                : target!.Character.PositionX,
            selfArea
                ? attacker.Character.PositionZ
                : target!.Character.PositionZ);
    }
}
