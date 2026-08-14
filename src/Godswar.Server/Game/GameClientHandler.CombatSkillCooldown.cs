using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool TryClaimHostileSkillCooldown(
        GameCharacter character,
        SkillCombatDefinition combat,
        DateTimeOffset observedAt,
        out OwnedHostileSkillCooldownLease lease)
    {
        if (_registry.TryClaimHostileSkillCooldown(
                _session,
                character,
                checked((uint)combat.SkillId),
                combat.Cooldown,
                observedAt,
                out lease,
                out var readyAt))
        {
            return true;
        }

        var remaining = Math.Max(
            0d,
            (readyAt - observedAt).TotalSeconds);
        Console.WriteLine(
            $"[skill] rejected cooldown character={_character?.Name} " +
            $"skill={combat.SkillId} remaining={remaining:F2}");
        return false;
    }

    private void ReleaseHostileSkillCooldown(
        in OwnedHostileSkillCooldownLease lease) =>
        _registry.ReleaseHostileSkillCooldown(lease);

    private bool TryReserveLegacyHostileSkill(
        GameCharacter character,
        SkillCombatDefinition combat,
        DateTimeOffset observedAt,
        out int currentMana,
        out OwnedHostileSkillCooldownLease cooldownLease,
        out bool cooldownRejected)
    {
        var manaCost = Math.Max(0, combat.Mp);
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            cooldownLease = default;
            cooldownRejected = false;
            if (currentMana < manaCost)
            {
                return false;
            }
        }

        if (!TryClaimHostileSkillCooldown(
                character,
                combat,
                observedAt,
                out cooldownLease))
        {
            cooldownRejected = true;
            return false;
        }

        var reserved = false;
        lock (character.VitalsSync)
        {
            currentMana = character.CurrentMp;
            if (currentMana < manaCost)
            {
                reserved = false;
            }
            else
            {
                character.CurrentMp = currentMana - manaCost;
                currentMana = character.CurrentMp;
                if (manaCost > 0)
                {
                    character.MarkVitalsChanged();
                }

                reserved = true;
            }
        }

        if (!reserved)
        {
            ReleaseHostileSkillCooldown(cooldownLease);
        }

        return reserved;
    }
}
