using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private PlayerSkillCastControl ResolvePlayerSkillCastControl(
        DateTimeOffset observedAt,
        bool pendingCompletion = false)
    {
        var existing = _registry.GetPlayerSkillCastControl(
            _session,
            observedAt);
        if (existing != PlayerSkillCastControl.None)
        {
            return existing;
        }

        var hostile = _registry.GetTrainingDummyHostileControl(
            _session,
            observedAt);
        if ((hostile & HostileStatusControlFlags.NonAttackUsing) != 0)
        {
            return PlayerSkillCastControl.Stunned;
        }
        if ((hostile &
             (HostileStatusControlFlags.NonMagicUsing |
              HostileStatusControlFlags.NonTechniqueUsing)) != 0)
        {
            return PlayerSkillCastControl.Silenced;
        }
        if (pendingCompletion &&
            (hostile & HostileStatusControlFlags.HaltIntonate) != 0)
        {
            return PlayerSkillCastControl.Stunned;
        }

        if (!IsMedusaActionAllowed(
                MedusaEncounterControlRestriction.SkillCast,
                observedAt,
                "skill cast"))
        {
            return PlayerSkillCastControl.Stunned;
        }

        return PlayerSkillCastControl.None;
    }

    private bool IsHostileStatusBasicAttackAllowed(
        DateTimeOffset observedAt)
    {
        var hostile = _registry.GetTrainingDummyHostileControl(
            _session,
            observedAt);
        if ((hostile & HostileStatusControlFlags.NonAttackUsing) != 0)
        {
            Console.WriteLine(
                "[status] basic attack blocked " +
                $"character={_character?.Name} control={hostile}");
            return false;
        }

        return IsMedusaActionAllowed(
            MedusaEncounterControlRestriction.BasicAttack,
            observedAt,
            "basic attack");
    }

    private bool IsHostileStatusItemUseAllowed(
        DateTimeOffset observedAt) => IsMedusaActionAllowed(
            MedusaEncounterControlRestriction.ItemUse,
            observedAt,
            "item use");

    private bool IsMedusaActionAllowed(
        MedusaEncounterControlRestriction action,
        DateTimeOffset observedAt,
        string actionName)
    {
        var allowed = _registry.IsMedusaActionAllowed(
            _session,
            action,
            observedAt,
            out var authority);
        if (!allowed)
        {
            Console.WriteLine(
                "[medusa-status] action blocked " +
                $"character={_character?.Name} action={actionName} " +
                $"authority={authority.Outcome} " +
                $"control={authority.View?.ControlRestriction}");
        }

        return allowed;
    }
}
