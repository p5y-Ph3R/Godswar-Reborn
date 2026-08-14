using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Replaces the session's combat-facing pet projection with at most one
    /// authoritative carried-and-summoned pet. The durable collection remains
    /// owned by the character snapshot and is never retained by combat ECS.
    /// </summary>
    internal bool UpdateActivePetHealingRuntime(
        ClientSession session,
        IReadOnlyList<PetBootstrapSnapshot> pets)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(pets);
        if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context))
            {
                return false;
            }

            PetHealingTalentHydrationSnapshot? active = null;
            foreach (var pet in pets)
            {
                if (!pet.IsCarried || !pet.IsSummoned)
                {
                    continue;
                }

                if (active is not null)
                {
                    throw new InvalidDataException(
                        "A character snapshot cannot expose multiple " +
                        "active Healing pets.");
                }
                if (pet.AccountId != context.AccountId ||
                    pet.OwnerCharacterId != context.CharacterId ||
                    pet.PetId <= 0 ||
                    pet.PetId > uint.MaxValue ||
                    pet.Level <= 0 ||
                    (short)pet.Aptitude is < 1 or > 16 ||
                    pet.TalentMask is < 0 or > 31)
                {
                    throw new InvalidDataException(
                        "The active pet Healing projection is outside " +
                        "its authoritative identity or scalar bounds.");
                }

                active = new PetHealingTalentHydrationSnapshot(
                    pet.PetId,
                    pet.Level,
                    (short)pet.Aptitude,
                    pet.TalentMask,
                    pet.IsCarried,
                    pet.IsSummoned);
            }

            GetPlayerRuntimeEcs(session)
                .IncomingDamage.UpdateActivePet(active);
            return true;
        }
    }

}
