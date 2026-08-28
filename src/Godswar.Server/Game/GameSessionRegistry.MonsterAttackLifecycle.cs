namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private PlayerStatusState? TryRetainMonsterAttackStatusState(
        GameSessionContext? statusContext)
    {
        if (statusContext is null ||
            !_playerStatusStates.TryGetValue(
                statusContext.Session,
                out var state))
        {
            return null;
        }

        // Status mutations use status-gate -> registry-gate ordering. Retain
        // an existing state before the primary transaction so lethal Ride
        // removal cannot wait/yield after HP. Never create state for an event
        // whose emitted target authority has not yet been validated.
        state.Gate.Wait();
        return state;
    }
}
