using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleBasicAttackEcsAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (!BasicAttackRequest.TryParse(packet.Buffer, out var attack))
        {
            Console.WriteLine(
                $"[attack] ignored malformed basic attack len={packet.Length} hex={packet.ToHexPreview()}");
            return;
        }

        if (attack.AttackerObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                $"[attack] rejected spoofed attacker character={character.Name} supplied={attack.AttackerObjectId} expected={LocalPlayerObjectId}");
            return;
        }

        var decision = _registry.ResolvePlayerCombatEcs(
            _session,
            character,
            LocalPlayerObjectId,
            _nextBasicAttackAt,
            PlayerCombatEcsRequest.BasicAttack(
                DateTimeOffset.UtcNow,
                attack.TargetObjectId,
                attack.AttackerX,
                attack.AttackerZ));
        _nextBasicAttackAt = decision.NextBasicAttackAt;

        if (!decision.IntentAccepted)
        {
            LogBasicAttackEcsRejection(
                character,
                attack,
                decision.RejectionReason);
            return;
        }

        if (decision.Hits.Length != 1)
        {
            Console.WriteLine(
                $"[attack] rejected stale monster character={character.Name} target={attack.TargetObjectId}");
            return;
        }

        var hit = decision.Hits[0];
        var damageResult = hit.Result;
        var reportedDamage = hit.ReportedDamage;
        var pendingReward = damageResult.Killed
            ? await PrepareMonsterKillRewardAsync(damageResult)
            : null;
        var attackSelector = character.Profession is 2 or 3
            ? (byte)5
            : (byte)3;
        var selfPacket = PacketBuilder.PhysicalDamage(
            LocalPlayerObjectId,
            0f,
            0f,
            0f,
            attack.TargetObjectId,
            reportedDamage,
            result: attackSelector);
        var casterNotified = true;
        try
        {
            await _registry.DeliverMonsterHealthPacketToViewerAsync(
                _session,
                character.CurrentMap,
                attack.TargetObjectId,
                selfPacket,
                damageResult.HealthMutation!.Value,
                cancellationToken,
                "BasicAttackSelf");
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException)
        {
            casterNotified = false;
            Console.WriteLine(
                $"[attack] caster notification failed character={character.Name} target={attack.TargetObjectId}: {ex.Message}");
        }

        var worldObjectId = WorldObjectIds.ForPlayer(character.Id);
        var viewers = await _registry.BroadcastToMonsterViewersAsync(
            character.CurrentMap,
            attack.TargetObjectId,
            PacketBuilder.PhysicalDamage(
                worldObjectId,
                0f,
                0f,
                0f,
                attack.TargetObjectId,
                reportedDamage,
                result: attackSelector),
            cancellationToken,
            _session,
            "BasicAttackWorld",
            healthMutation: damageResult.HealthMutation);

        if (pendingReward is not null)
        {
            await PublishMonsterKillRewardAsync(
                pendingReward,
                cancellationToken);
        }

        Console.WriteLine(
            $"[attack] damage character={character.Name} target={attack.TargetObjectId} resolved={reportedDamage} applied={damageResult.BeforeHealth - damageResult.AfterHealth} hp={damageResult.AfterHealth}/{damageResult.Monster.MaximumHealth} killed={damageResult.Killed} caster-notified={casterNotified} viewers={viewers}");
    }

    private void LogBasicAttackEcsRejection(
        State.GameCharacter character,
        in BasicAttackRequest attack,
        PlayerCombatRejectionReason reason)
    {
        switch (reason)
        {
            case PlayerCombatRejectionReason.SourceDead:
                Console.WriteLine(
                    $"[attack] ignored basic attack from dead character={character.Name}");
                break;
            case PlayerCombatRejectionReason.InvalidCoordinates:
                Console.WriteLine(
                    $"[attack] rejected mismatched position character={character.Name} server={character.PositionX:F2},{character.PositionZ:F2} reported={attack.AttackerX:F2},{attack.AttackerZ:F2}");
                break;
            case PlayerCombatRejectionReason.OutOfRange:
                if (_registry.TryGetMonsterSnapshot(
                        character.CurrentMap,
                        attack.TargetObjectId,
                        out var target))
                {
                    Console.WriteLine(
                        $"[attack] rejected out-of-range monster character={character.Name} target={attack.TargetObjectId} player={attack.AttackerX:F2},{attack.AttackerZ:F2} monster={target.X:F2},{target.Z:F2}");
                }
                else
                {
                    Console.WriteLine(
                        $"[attack] rejected out-of-range monster character={character.Name} target={attack.TargetObjectId}");
                }

                break;
            case PlayerCombatRejectionReason.CooldownActive:
                Console.WriteLine(
                    $"[attack] rejected cooldown character={character.Name} target={attack.TargetObjectId}");
                break;
            case PlayerCombatRejectionReason.TargetUnavailable:
            case PlayerCombatRejectionReason.TargetGenerationMismatch:
            case PlayerCombatRejectionReason.TargetRevisionMismatch:
                Console.WriteLine(
                    $"[attack] rejected unavailable monster character={character.Name} target={attack.TargetObjectId}");
                break;
            default:
                Console.WriteLine(
                    $"[attack] rejected stale monster character={character.Name} target={attack.TargetObjectId}");
                break;
        }
    }
}
