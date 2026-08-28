using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsCurrentPlayerCombatTarget(uint objectId) =>
        _character is not null &&
        _registry.TryGetCurrentWorldSessionByObjectId(
            _session,
            _character.CurrentMap,
            objectId,
            out _);

    private async Task<bool> TryHandlePvpBasicAttackAsync(
        Protocol.GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            _character.CurrentHp <= 0 ||
            !BasicAttackRequest.TryParse(packet.Buffer, out var attack) ||
            attack.AttackerObjectId != LocalPlayerObjectId ||
            !IsCurrentPlayerCombatTarget(attack.TargetObjectId))
        {
            return false;
        }

        await HandlePvpBasicAttackAsync(
            attack,
            cancellationToken);
        return true;
    }

    private async Task HandlePvpBasicAttackAsync(
        BasicAttackRequest attack,
        CancellationToken cancellationToken)
    {
        var character = _character;
        if (character is null || character.CurrentHp <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextBasicAttackAt)
        {
            Console.WriteLine(
                $"[pvp] rejected cooldown character={character.Name} " +
                $"target={attack.TargetObjectId}");
            return;
        }

        var interruptAdmittedCast = false;
        long AdmitAttack()
        {
            var revision = NextAdmittedLegacyCombatRevision();
            interruptAdmittedCast = true;
            return revision;
        }

        var decision = await _registry.ResolvePvpBasicAttackAsync(
            _session,
            attack.TargetObjectId,
            attack.AttackerX,
            attack.AttackerZ,
            AdmitAttack,
            now,
            cancellationToken,
            admittedAttemptBarrier: () => interruptAdmittedCast
                ? InterruptPendingSkillCastAsync(
                    SkillCastInterruptionReason.Replaced,
                    cancellationToken)
                : null);
        if (!decision.Accepted)
        {
            Console.WriteLine(
                $"[pvp] rejected character={character.Name} " +
                $"target={attack.TargetObjectId} " +
                $"reason={decision.RejectionReason} " +
                $"eligibility={decision.Eligibility.Failure}");
            return;
        }

        var stats = CharacterStats.FromCharacter(character);
        _nextBasicAttackAt = now +
            PlayerCombatRules.ResolveBasicAttackCooldown(
                stats.BasicAttackIntervalMilliseconds);
        Console.WriteLine(
            $"[pvp] attack character={character.Name} " +
            $"target={decision.Target?.DisplayName} " +
            $"event={decision.Resolution.EventId} " +
            $"outcome={decision.Resolution.Outcome} " +
            $"resolved={decision.Resolution.Damage} " +
            $"applied={decision.AppliedDamage} " +
            $"life-steal={decision.LifeAbsorptionHealing} " +
            $"rebound={decision.ReboundDamage} " +
            $"target-hp={decision.TargetCurrentHealth} " +
            $"attacker-hp={decision.AttackerCurrentHealth}");
    }
}
