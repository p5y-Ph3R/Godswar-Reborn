using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal long AdjustElementalHealingReceived(
        ClientSession targetSession,
        GameCharacter targetCharacter,
        DateTimeOffset authoritativeAt,
        long requestedHealing)
    {
        ArgumentNullException.ThrowIfNull(targetSession);
        ArgumentNullException.ThrowIfNull(targetCharacter);
        if (requestedHealing <= 0)
        {
            return 0;
        }

        ElementalCombatSessionFence fence;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(targetSession, out var context) ||
                !context.WorldReady ||
                !ReferenceEquals(context.Character, targetCharacter) ||
                context.CharacterId != targetCharacter.Id ||
                !IsCurrentAccountSession(
                    context.AccountId,
                    targetSession,
                    context.Ownership))
            {
                return requestedHealing;
            }

            fence = new(
                context.CharacterId,
                context.MapId,
                context.Ownership);
        }

        return TryGetElementalStatusAdjustment(
            targetSession,
            fence,
            authoritativeAt.ToUnixTimeMilliseconds(),
            movementSpeed: 0,
            physicalDefense: 0,
            magicDefense: 0,
            hitRating: 0,
            healingReceived: requestedHealing,
            out var adjustment)
            ? adjustment.HealingReceived
            : requestedHealing;
    }
}
