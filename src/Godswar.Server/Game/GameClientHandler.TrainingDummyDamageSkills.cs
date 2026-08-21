using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleTrainingDummyDamageScalarAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition skill,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return;
        }

        if (cast.CasterObjectId != LocalPlayerObjectId)
        {
            Console.WriteLine(
                "[training-skill] rejected non-local caster " +
                $"character={character.Name} caster={cast.CasterObjectId}");
            return;
        }

        var decision = await _registry
            .ResolveTrainingDummyDamageScalarAsync(
                _session,
                cast.CasterObjectId,
                cast.TargetObjectId,
                packet.Buffer,
                skill,
                NextAdmittedLegacyCombatRevision,
                DateTimeOffset.UtcNow,
                cancellationToken);
        if (!decision.Accepted)
        {
            Console.WriteLine(
                "[training-skill] rejected " +
                $"character={character.Name} skill={cast.SkillId} " +
                $"target={cast.TargetObjectId} " +
                $"reason={decision.RejectionReason}");
            await PublishTrainingSkillManaRejectionAsync(
                decision.RejectionReason,
                decision.CurrentMana,
                cancellationToken);
            return;
        }

        Console.WriteLine(
            "[training-skill] authored " +
            TrainingDummyDamageSkillPolicy.DisplayName(
                _gameplayCatalogs,
                skill.SkillId) +
            $" character={character.Name} " +
            $"target={decision.Combat.Target?.DisplayName} " +
            $"event={decision.Combat.Resolution.EventId} " +
            $"outcome={decision.Combat.Resolution.Outcome} " +
            $"resolved={decision.Combat.Resolution.Damage} " +
            $"applied={decision.Combat.AppliedDamage} " +
            $"mp={decision.CurrentMana}");
    }

    private async Task<bool> TryHandleTrainingDummyDamageAreaAsync(
        GamePacket packet,
        SkillCastRequest cast,
        SkillCombatDefinition skill,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null)
        {
            return false;
        }

        var decision = await _registry
            .ResolveTrainingDummyDamageAreaAsync(
                _session,
                cast.CasterObjectId,
                packet.Buffer,
                skill,
                NextAdmittedLegacyCombatRevision,
                DateTimeOffset.UtcNow,
                cancellationToken);
        if (!decision.Handled)
        {
            return false;
        }
        if (!decision.Accepted)
        {
            Console.WriteLine(
                "[training-skill] rejected area " +
                $"character={character.Name} skill={cast.SkillId} " +
                $"reason={decision.RejectionReason} " +
                $"committed={decision.Combats.Count}");
            await PublishTrainingSkillManaRejectionAsync(
                decision.RejectionReason,
                decision.CurrentMana,
                cancellationToken);
            return true;
        }

        var hits = decision.Combats.Count(static combat =>
            combat.Resolution.Hit);
        var applied = decision.Combats.Aggregate(
            0UL,
            static (total, combat) => total + combat.AppliedDamage);
        Console.WriteLine(
            "[training-skill] authored " +
            TrainingDummyDamageSkillPolicy.DisplayName(
                _gameplayCatalogs,
                skill.SkillId) +
            $" character={character.Name} " +
            $"targets={decision.Combats.Count} " +
            $"hits={hits} " +
            $"applied={applied} " +
            $"mp={decision.CurrentMana}");
        return true;
    }

    private async Task PublishTrainingSkillManaRejectionAsync(
        TrainingDummySkillRejectionReason reason,
        int currentMana,
        CancellationToken cancellationToken)
    {
        if (reason is not (
                TrainingDummySkillRejectionReason.InsufficientMana or
                TrainingDummySkillRejectionReason.PartialCommitFailure))
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PlayerManaUpdate(
                LocalPlayerObjectId,
                currentMana),
            cancellationToken,
            "TrainingDummySkillManaRejected");
    }
}
