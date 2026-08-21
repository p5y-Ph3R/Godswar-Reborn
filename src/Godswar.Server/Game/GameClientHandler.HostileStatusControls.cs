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

        return PlayerSkillCastControl.None;
    }

    private bool IsHostileStatusBasicAttackAllowed(
        DateTimeOffset observedAt)
    {
        var hostile = _registry.GetTrainingDummyHostileControl(
            _session,
            observedAt);
        if ((hostile & HostileStatusControlFlags.NonAttackUsing) == 0)
        {
            return true;
        }

        Console.WriteLine(
            "[status] basic attack blocked " +
            $"character={_character?.Name} control={hostile}");
        return false;
    }
}
