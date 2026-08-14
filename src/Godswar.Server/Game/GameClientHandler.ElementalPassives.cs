using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private static void ApplyElementalPassiveStats(
        GameCharacter character,
        CharacterStats baseStats)
    {
        var passive = ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
            character.ElementalEquipment,
            Math.Max(1, baseStats.MaxHp),
            movementSpeed: 0);
        var maximumHealth = checked((int)Math.Min(
            passive.MaximumHealth,
            int.MaxValue));
        lock (character.VitalsSync)
        {
            character.MaxHp = Math.Max(1, maximumHealth);
            character.CurrentHp = Math.Clamp(
                character.CurrentHp,
                0,
                character.MaxHp);
        }
    }
}
